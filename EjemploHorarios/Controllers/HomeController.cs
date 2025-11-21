using EjemploHorarios.Models;
using EjemploHorarios.Models.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Web;
using System.Web.Mvc;





namespace EjemploHorarios.Controllers
{
    public class HomeController : Controller
    {
        private readonly SenaPlanningEntities1 db = new SenaPlanningEntities1();

        // GET: Home/Index
        public ActionResult Index()
        {

            ActualizarEstadosFichas();
            return View();
        }
        public ActionResult ListaHorarios()
        {
            return View();
        }


        [HttpGet]
        public JsonResult GetFichasEnFormacion(string term = "", int anio = 0, int trimestre = 0)
        {
            try
            {
                // 🔹 2. Validar parámetros
                if (anio <= 0 || trimestre < 1 || trimestre > 4)
                    return Json(new { ok = false, msg = "Parámetros inválidos." }, JsonRequestBehavior.AllowGet);

                // 🔹 3. Calcular rango del trimestre del año solicitado
                var inicioTrimestre = new DateTime(anio, ((trimestre - 1) * 3) + 1, 1);
                var finTrimestre = inicioTrimestre.AddMonths(3).AddDays(-1);

                // 🔹 4. Rango extendido (6 meses antes y después)
                var inicioRango = inicioTrimestre.AddMonths(-6);
                var finRango = finTrimestre.AddMonths(6);

                term = (term ?? "").Trim();

                // 🔹 5. Consultar fichas activas (EstadoFicha = true)
                var fichasQuery = from f in db.Ficha
                                  join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma
                                  where f.FechaInFicha.HasValue
                                        && f.FechaFinFicha.HasValue
                                        && f.EstadoFicha == true
                                        && f.FechaFinFicha.Value >= inicioRango
                                        && f.FechaInFicha.Value <= finRango
                                  select new
                                  {
                                      f.IdFicha,
                                      f.CodigoFicha,
                                      f.IdPrograma,
                                      ProgramaNombre = p.DenominacionPrograma,
                                      TrimestreDeLaFicha = f.Trimestre,
                                      f.FechaInFicha,
                                      f.FechaFinFicha
                                  };

                // 🔹 6. Aplicar búsqueda en memoria (para coincidencias con 'term')
                var fichas = fichasQuery
                    .AsEnumerable()
                    .Where(f =>
                        string.IsNullOrEmpty(term)
                        || (f.CodigoFicha != null && f.CodigoFicha.ToString().IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (!string.IsNullOrEmpty(f.ProgramaNombre) && f.ProgramaNombre.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    )
                    .OrderBy(f => f.CodigoFicha)
                    .ToList();

                if (!fichas.Any())
                    return Json(new { ok = false, msg = "No se encontraron fichas lectivas para ese año y trimestre." },
                                JsonRequestBehavior.AllowGet);

                return Json(new { ok = true, data = fichas }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "❌ Error al obtener las fichas: " + ex.Message },
                            JsonRequestBehavior.AllowGet);
            }
        }







        // 🧩 MÉTODO PRIVADO: actualización automática de estados lectivo / práctica
        private void ActualizarEstadosFichas()
        {
            try
            {
                var hoy = DateTime.Now.Date;

                // 🔥 Fichas que podrían cambiar hoy (FechaFinFicha no nula)
                var fichas = db.Ficha
                               .Where(f => f.FechaFinFicha.HasValue)
                               .ToList();

                foreach (var ficha in fichas)
                {
                    DateTime fechaLimite = ficha.FechaFinFicha.Value.AddMonths(-6);

                    bool nuevoEstado = hoy <= fechaLimite;

                    if (ficha.EstadoFicha != nuevoEstado)
                    {
                        ficha.EstadoFicha = nuevoEstado;
                    }
                }

                db.SaveChanges();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("⚠️ Error al actualizar estados: " + ex.Message);
            }
        }



        [HttpPost]
        public JsonResult ImportarExcel(HttpPostedFileBase archivoExcel, int anio, int trimestre, int idFicha)
        {
            try
            {
                if (archivoExcel == null || archivoExcel.ContentLength == 0)
                    return Json(new { ok = false, msg = "No se cargó ningún archivo Excel." });

                string path = Server.MapPath("~/Uploads/");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);

                string filePath = Path.Combine(path, Path.GetFileName(archivoExcel.FileName));
                archivoExcel.SaveAs(filePath);

                var ficha = db.Ficha.Include("Programa_Formacion").FirstOrDefault(f => f.IdFicha == idFicha);
                if (ficha == null)
                    return Json(new { ok = false, msg = "Ficha no encontrada." });

                // ❌ YA NO ACTUALIZAMOS EL TRIMESTRE DE LA FICHA
                // ficha.Trimestre = trimestre;   ← ELIMINADO
                // db.SaveChanges();              ← ELIMINADO

                // 👍 SOLO usamos el trimestre que viene desde el usuario
                int horarioId = ObtenerHorarioValido(idFicha, anio, trimestre);

                string programaNombre = ficha.Programa_Formacion?.DenominacionPrograma ?? "Programa desconocido";

                using (var package = new OfficeOpenXml.ExcelPackage(new FileInfo(filePath)))
                {
                    var ws = package.Workbook.Worksheets["Hoja1"];
                    if (ws == null)
                        return Json(new { ok = false, msg = "No se encontró la hoja 'Hoja1'." });

                    int rowCount = ws.Dimension.Rows;
                    string competenciaActual = null;

                    for (int row = 2; row <= rowCount; row++)
                    {
                        string competencia = ws.Cells[row, 4].Text?.Trim();
                        string resultado = ws.Cells[row, 6].Text?.Trim();
                        string instructorNombre = ws.Cells[row, 35].Text?.Trim();

                        if (!string.IsNullOrEmpty(competencia))
                            competenciaActual = competencia;

                        if (string.IsNullOrEmpty(competenciaActual) || string.IsNullOrEmpty(resultado))
                            continue;

                        int idInstructorTemp = ObtenerInstructorId(instructorNombre);
                        int? idInstructorFinal = idInstructorTemp == 0 ? (int?)null : idInstructorTemp;

                        var registro = new Diseño_Curricular
                        {
                            Competencia = competenciaActual,
                            Orden = ParseNullableInt(ws.Cells[row, 5].Text),
                            Resultado = resultado,
                            Duracion = ParseNullableInt(ws.Cells[row, 7].Text),
                            HrTrimI = ParseNullableInt(ws.Cells[row, 8].Text),
                            HrTrimII = ParseNullableInt(ws.Cells[row, 9].Text),
                            HrTrimIII = ParseNullableInt(ws.Cells[row, 10].Text),
                            HrTrimIV = ParseNullableInt(ws.Cells[row, 11].Text),
                            HrTrimV = ParseNullableInt(ws.Cells[row, 12].Text),
                            HrTrimVI = ParseNullableInt(ws.Cells[row, 13].Text),
                            HrTrimVII = ParseNullableInt(ws.Cells[row, 14].Text),
                            Total_Hr = ParseNullableInt(ws.Cells[row, 15].Text),
                            Prog = programaNombre,
                            IdInstructor = idInstructorFinal ?? 1219,
                            Id_Horario = horarioId,
                            IdFicha = idFicha
                        };

                        db.Diseño_Curricular.Add(registro);
                    }

                    db.SaveChanges();
                }

                // 🎯 USAR SIEMPRE EL TRIMESTRE QUE EL USUARIO SELECCIONÓ
                var competenciasFiltradas = FiltrarCompetenciasPorTrimestre(idFicha, trimestre);

                return Json(new
                {
                    ok = true,
                    msg = "✅ Competencias cargadas y filtradas correctamente.",
                    competencias = competenciasFiltradas
                });
            }
            catch (Exception ex)
            {
                string deepMsg = ex.InnerException?.InnerException?.Message
                                 ?? ex.InnerException?.Message
                                 ?? ex.Message;
                return Json(new { ok = false, msg = "❌ Error al procesar el archivo: " + deepMsg });
            }
        }

        public class CompetenciaDTO
        {
            public string Competencia { get; set; }
            public List<string> Resultados { get; set; }
        }

        private int ObtenerInstructorId(string nombre)
        {
            // 1️⃣ Si viene vacío → usar instructor genérico
            if (string.IsNullOrWhiteSpace(nombre))
                return 1219;

            // 2️⃣ Lista de valores basura
            string[] basura =
            {
        "100%", "%", "NO", "N/A", "-", "--", "0", "XX", "XXX",
        "NINGUNO", "SIN", "NO APLICA", "NOAPLICA",
        "INSTRUCTOR", "INSTRUCTOR GENERICO", "INSTRUCTOR GENÉRICO"
    };

            string upper = nombre.Trim().ToUpperInvariant();
            if (basura.Contains(upper))
                return 1219;

            // 3️⃣ Normalizador (SIN usar dentro de LINQ)
            string Normalizar(string t)
            {
                if (string.IsNullOrWhiteSpace(t)) return "";

                t = t.Trim().ToUpperInvariant();

                while (t.Contains("  "))
                    t = t.Replace("  ", " ");

                // Quitar tildes
                var normalized = t.Normalize(System.Text.NormalizationForm.FormD);
                var sb = new System.Text.StringBuilder();

                foreach (char c in normalized)
                {
                    var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                    if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                        sb.Append(c);
                }

                return sb.ToString().Trim();
            }

            // Normalize del Excel
            string nombreExcelNorm = Normalizar(nombre);

            // 4️⃣ Cargar todos los instructores a memoria (AQUÍ SI PODEMOS USAR TOUPPER, etc)
            var lista = db.Instructor
                          .AsNoTracking()
                          .ToList()
                          .Select(i => new
                          {
                              Id = i.IdInstructor,
                              NombreNorm = Normalizar(i.NombreCompletoInstructor)
                          })
                          .ToList();

            // 5️⃣ Buscar coincidencia EXACTA
            var exacto = lista.FirstOrDefault(x => x.NombreNorm == nombreExcelNorm);
            if (exacto != null)
                return exacto.Id;

            // 6️⃣ Buscar coincidencia por palabras (muy útil)
            string[] palabrasExcel = nombreExcelNorm.Split(' ').Where(x => x.Length > 0).ToArray();

            foreach (var item in lista)
            {
                string[] palabrasBD = item.NombreNorm.Split(' ').Where(x => x.Length > 0).ToArray();

                int coincidencias = palabrasExcel.Count(pe => palabrasBD.Contains(pe));

                if (coincidencias >= 2) // regla segura
                    return item.Id;
            }

            // 7️⃣ Si no lo encuentra
            return 1219;
        }





        [HttpGet]
        public JsonResult GetCompetenciasPorTrimestre(int idFicha, int trimestre)
        {
            if (idFicha <= 0 || trimestre < 1 || trimestre > 7)
                return Json(new { ok = false, msg = "Parámetros inválidos." }, JsonRequestBehavior.AllowGet);

            // 🔹 Calcular el trimestre siguiente (máximo 7)
            int trimestreSiguiente = (trimestre < 7) ? trimestre + 1 : 7;

            var data = FiltrarCompetenciasPorTrimestre(idFicha, trimestreSiguiente);

            if (!data.Any())
                return Json(new { ok = false, msg = "No hay resultados de aprendizaje para el trimestre siguiente." },
                            JsonRequestBehavior.AllowGet);

            return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
        }

        private List<CompetenciaDTO> FiltrarCompetenciasPorTrimestre(int idFicha, int trimestre)
        {
            var registros = db.Diseño_Curricular
                .Where(c => c.IdFicha == idFicha)
                .ToList();

            var competencias = registros
                .GroupBy(c => c.Competencia)
                .Select(g => new CompetenciaDTO
                {
                    Competencia = g.Key,
                    Resultados = g
                        .Where(r =>
                            (trimestre == 1 && (r.HrTrimI ?? 0) > 0) ||
                            (trimestre == 2 && (r.HrTrimII ?? 0) > 0) ||
                            (trimestre == 3 && (r.HrTrimIII ?? 0) > 0) ||
                            (trimestre == 4 && (r.HrTrimIV ?? 0) > 0) ||
                            (trimestre == 5 && (r.HrTrimV ?? 0) > 0) ||
                            (trimestre == 6 && (r.HrTrimVI ?? 0) > 0) ||
                            (trimestre == 7 && (r.HrTrimVII ?? 0) > 0)
                        )
                        .Select(r => r.Resultado)
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Distinct()
                        .ToList()
                })
                .Where(c => c.Resultados.Any())
                .ToList();

            return competencias;
        }



        private int ObtenerHorarioValido(int idFicha, int anio, int trimestre)
        {
            // Buscar si ya existe un horario para esa ficha y trimestre
            var horarioExistente = db.Horario.FirstOrDefault(h => h.IdFicha == idFicha && h.Trimestre_Año == trimestre);
            if (horarioExistente != null)
                return horarioExistente.Id_Horario;

            // 🔹 Buscar un instructor válido (al menos uno existente)
            var instructor = db.Instructor.FirstOrDefault();
            if (instructor == null)
                throw new Exception("No hay instructores registrados en la base de datos.");

            // 🔹 Crear una asignación base (necesaria para cumplir la FK)
            var asignacion = new Asignacion_horario
            {
                Dia = "Pendiente",
                HoraDesde = new TimeSpan(6, 0, 0),  // 06:00 AM
                HoraHasta = new TimeSpan(8, 0, 0),  // 08:00 AM
                IdInstructor = instructor.IdInstructor,
                IdFicha = idFicha                   // 👈 IMPORTANTE: este campo ahora es requerido
            };

            db.Asignacion_horario.Add(asignacion);
            db.SaveChanges(); // ✅ Aquí se genera el Id_Asignacion real

            // 🔹 Crear el horario enlazado con esa asignación
            var nuevoHorario = new Horario
            {
                IdFicha = idFicha,
                Año_Horario = anio,
                Trimestre_Año = trimestre,
                Fecha_Creacion = DateTime.Now,
                Id_Asignacion = asignacion.Id_Asignacion // 👈 Aquí usamos el ID recién creado
            };

            db.Horario.Add(nuevoHorario);
            db.SaveChanges();

            return nuevoHorario.Id_Horario;
        }


        private int? ParseNullableInt(string text)
        {
            int val;
            return int.TryParse(text?.Trim(), out val) ? (int?)val : null;
        }

        private decimal? ParseNullableDecimal(string text)
        {
            decimal val;
            return decimal.TryParse(text?.Trim().Replace(",", "."), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out val) ? (decimal?)val : null;
        }

        [HttpGet]
        public JsonResult GetResultadosPorCompetencia(string nombreCompetencia)
        {
            var resultados = db.Diseño_Curricular
                .Where(c => c.Competencia == nombreCompetencia)
                .Select(c => new { c.Resultado, c.Duracion, c.HrTrimI, c.HrTrimII, c.HrTrimIII })
                .ToList();

            return Json(new { ok = true, resultados }, JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        public JsonResult GetInstructores(string q = null, int? top = null)
        {
            try
            {
                // Si tienes auth global y este endpoint debe ser público:
                // [AllowAnonymous] sobre el método (o quita el filtro para esta acción)

                var query = db.Instructor.AsNoTracking()
                             .Where(i => i.EstadoInstructor == true);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim().ToLower();
                    // Evita nulls en NombreCompletoInstructor
                    query = query.Where(i => (i.NombreCompletoInstructor ?? "").ToLower().Contains(term));
                }

                query = query.OrderBy(i => i.NombreCompletoInstructor);

                if (top.HasValue && top.Value > 0)
                    query = query.Take(top.Value);

                var data = query.Select(i => new
                {
                    id = i.IdInstructor,
                    nombre = i.NombreCompletoInstructor ?? "(Sin nombre)"
                })
                            .ToList();

                // Fuerza tipo de contenido JSON y 200
                Response.ContentType = "application/json";
                Response.StatusCode = 200;

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                // Devuelve JSON también en error
                Response.ContentType = "application/json";
                Response.StatusCode = 200; // o 500 si prefieres manejarlo en el cliente
                return Json(new { ok = false, msg = "Error al obtener instructores: " + ex.Message },
                            JsonRequestBehavior.AllowGet);
            }
        }
        [HttpGet]
        public JsonResult GetInstructorPorResultado(string resultado)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(resultado))
                {
                    return Json(new
                    {
                        ok = true,
                        data = new { IdInstructor = 0, Nombre = "Instructor no asignado" }
                    }, JsonRequestBehavior.AllowGet);
                }

                // ===== NORMALIZADOR =====
                string Normalize(string t)
                {
                    if (string.IsNullOrEmpty(t)) return "";
                    var normalized = t.Normalize(System.Text.NormalizationForm.FormD);
                    var sb = new System.Text.StringBuilder();
                    foreach (char c in normalized)
                    {
                        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                            System.Globalization.UnicodeCategory.NonSpacingMark)
                            sb.Append(c);
                    }
                    return sb.ToString().ToUpperInvariant().Trim();
                }

                // ===== LIMPIEZA =====
                string QuitarBasura(string tx)
                {
                    if (string.IsNullOrWhiteSpace(tx)) return "";
                    string[] basura = { "EL", "LA", "DE", "DEL", "LOS", "LAS", "Y", "EN", "PARA" };

                    return string.Join(" ",
                        tx.Split(' ')
                          .Where(p => p.Length > 3 && !basura.Contains(p))
                    );
                }

                string buscado = QuitarBasura(Normalize(resultado));

                // ============================
                // BUSCAR RESULTADO EXACTO / CERCA
                // ============================

                // Primero: coincidencia EXACTA normalizada
                var lista = db.Diseño_Curricular.ToList();

                var data = lista.FirstOrDefault(x =>
                {
                    string res = QuitarBasura(Normalize(x.Resultado));
                    return res == buscado;
                });

                // Segundo intento: contiene (pero ya no tan laxo)
                if (data == null)
                {
                    data = lista.FirstOrDefault(x =>
                    {
                        string res = QuitarBasura(Normalize(x.Resultado));
                        return res.Contains(buscado) || buscado.Contains(res);
                    });
                }

                // ============================
                // SI NO HUBO COINCIDENCIA
                // ============================
                if (data == null)
                {
                    return Json(new
                    {
                        ok = true,
                        data = new { IdInstructor = 0, Nombre = "Instructor no asignado" }
                    }, JsonRequestBehavior.AllowGet);
                }

                // ============================
                // SI HUBO → TRAER INSTRUCTOR
                // ============================
                var instructor = db.Instructor
                                   .FirstOrDefault(i => i.IdInstructor == data.IdInstructor);

                if (instructor == null)
                {
                    return Json(new
                    {
                        ok = true,
                        data = new { IdInstructor = 0, Nombre = "Instructor no asignado" }
                    }, JsonRequestBehavior.AllowGet);
                }

                // ============================
                // ÉXITO
                // ============================
                return Json(new
                {
                    ok = true,
                    data = new
                    {
                        IdInstructor = instructor.IdInstructor,
                        Nombre = instructor.NombreCompletoInstructor
                    }
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "Error: " + ex.Message },
                    JsonRequestBehavior.AllowGet);
            }
        }




        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult GuardarHorario(
     string AsignacionesJson,
     string numeroFicha,
     string nombreHorario,
     string trimestre,
     int idInstructorLider)
        {
            try
            {
                // ============================
                // 1. Deserializar asignaciones
                // ============================
                var asignaciones = JsonConvert.DeserializeObject<List<AsignacionViewModel>>(AsignacionesJson);
                if (asignaciones == null || !asignaciones.Any())
                    return Json(new { ok = false, msg = "⚠️ No hay asignaciones para guardar." });

                // ============================
                // 2. Obtener ficha
                // ============================
                var ficha = db.Ficha.FirstOrDefault(f => f.CodigoFicha.ToString() == numeroFicha);
                if (ficha == null)
                    return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                int tri = int.TryParse(trimestre, out var parsedTri) ? parsedTri : 0;

                var horarioExistente = db.Horario
                    .FirstOrDefault(h => h.IdFicha == ficha.IdFicha && h.Trimestre_Año == tri);

                int? primeraAsignacionId = null;
                const int SEMANAS_TRIMESTRE = 12;

                var listaAsignaciones = new List<Asignacion_horario>();

                // ============================
                // 3. Validación interna
                // ============================
                foreach (var grp in asignaciones
                    .Where(a => a.instructorId > 0)
                    .GroupBy(a => new { a.instructorId, a.dia }))
                {
                    var lista = grp.ToList();

                    for (int i = 0; i < lista.Count; i++)
                    {
                        for (int j = i + 1; j < lista.Count; j++)
                        {
                            var a1 = lista[i];
                            var a2 = lista[j];

                            TimeSpan d1 = TimeSpan.Parse(a1.horaDesde);
                            TimeSpan h1 = TimeSpan.Parse(a1.horaHasta);
                            TimeSpan d2 = TimeSpan.Parse(a2.horaDesde);
                            TimeSpan h2 = TimeSpan.Parse(a2.horaHasta);

                            bool choque =
                                (d1 >= d2 && d1 < h2) ||
                                (h1 > d2 && h1 <= h2) ||
                                (d1 <= d2 && h1 >= h2);

                            if (choque)
                            {
                                return Json(new
                                {
                                    ok = false,
                                    msg = $"❌ El instructor {grp.Key.instructorId} tiene dos rangos de horas traslapados en {grp.Key.dia}."
                                });
                            }
                        }
                    }
                }

                // *************************************************************
                // 4. CACHE PARA EVITAR DUPLICADOS REALMENTE (CLAVE UNICA)
                // *************************************************************
                Dictionary<string, HorarioInstructor> cacheHI = new Dictionary<string, HorarioInstructor>();

                // ============================
                // 5. Procesar asignaciones
                // ============================
                foreach (var a in asignaciones)
                {
                    if (a.instructorId <= 0) continue;

                    TimeSpan d = TimeSpan.Parse(a.horaDesde);
                    TimeSpan h = TimeSpan.Parse(a.horaHasta);

                    decimal horasPorDia = (decimal)(h - d).TotalHours;
                    decimal horasAsignacion = Math.Round(horasPorDia * SEMANAS_TRIMESTRE, 2);

                    var instructor = db.Instructor.FirstOrDefault(i => i.IdInstructor == a.instructorId);
                    if (instructor == null) continue;

                    decimal horasActuales = instructor.Horas_Trabajadas ?? 0;
                    decimal horasMaximas = instructor.Horas_De_Trabajo ?? 0;

                    if (horasActuales + horasAsignacion > horasMaximas)
                        return Json(new { ok = false, msg = $"❌ El instructor {instructor.NombreCompletoInstructor} supera su límite de horas." });

                    // ============================
                    // 6. Validar choque
                    // ============================
                    bool choqueBD = db.HorarioInstructor.Any(hh =>
                        hh.IdInstructor == a.instructorId &&
                        hh.Dia == a.dia &&
                        (
                            (d >= hh.HoraDesde && d < hh.HoraHasta) ||
                            (h > hh.HoraDesde && h <= hh.HoraHasta) ||
                            (d <= hh.HoraDesde && h >= hh.HoraHasta)
                        )
                    );

                    if (choqueBD)
                        return Json(new { ok = false, msg = $"❌ Choque detectado: {instructor.NombreCompletoInstructor} ya tiene clase el {a.dia}." });

                    // ============================
                    // 7. Actualizar horas trabajadas
                    // ============================
                    instructor.Horas_Trabajadas = horasActuales + horasAsignacion;
                    db.Entry(instructor).State = EntityState.Modified;

                    // ============================
                    // 8. Crear asignación (Asignacion_horario)
                    // ============================
                    var nuevaAsig = new Asignacion_horario
                    {
                        Dia = a.dia,
                        HoraDesde = d,
                        HoraHasta = h,
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha
                    };

                    listaAsignaciones.Add(nuevaAsig);

                    // ==========================================================
                    // 9. GUARDAR / ACTUALIZAR HORARIO INSTRUCTOR (SIN DUPLICAR)
                    // ==========================================================

                    string comp = a.competencia ?? "";
                    string res = a.resultado ?? "";

                    string key = $"{a.instructorId}|{a.dia}|{d}|{h}";

                    HorarioInstructor HI;

                    if (cacheHI.ContainsKey(key))
                    {
                        HI = cacheHI[key];
                    }
                    else
                    {
                        HI = db.HorarioInstructor.FirstOrDefault(x =>
                            x.IdInstructor == a.instructorId &&
                            x.Dia == a.dia &&
                            x.HoraDesde == d &&
                            x.HoraHasta == h
                        );

                        if (HI == null)
                        {
                            HI = new HorarioInstructor
                            {
                                IdInstructor = a.instructorId,
                                IdFicha = ficha.IdFicha,
                                Dia = a.dia,
                                HoraDesde = d,
                                HoraHasta = h,
                                Competencia = "",
                                Resultado = ""
                            };

                            db.HorarioInstructor.Add(HI);
                        }

                        cacheHI[key] = HI;
                    }

                    // --- Agregar textos sin duplicar ---
                    if (!HI.Competencia.Contains(comp))
                        HI.Competencia += (string.IsNullOrEmpty(HI.Competencia) ? "" : " | ") + comp;

                    if (!HI.Resultado.Contains(res))
                        HI.Resultado += (string.IsNullOrEmpty(HI.Resultado) ? "" : " | ") + res;

                    db.Entry(HI).State = HI.IdHorarioInstructor == 0
                        ? EntityState.Added
                        : EntityState.Modified;
                }

                // ============================
                // 10. Guardar asignaciones
                // ============================
                foreach (var a in listaAsignaciones)
                    db.Asignacion_horario.Add(a);

                db.SaveChanges();

                primeraAsignacionId = listaAsignaciones.First().Id_Asignacion;

                // ============================
                // 11. Crear o actualizar horario de ficha
                // ============================
                if (horarioExistente == null)
                {
                    db.Horario.Add(new Horario
                    {
                        Año_Horario = DateTime.Now.Year,
                        Trimestre_Año = tri,
                        Fecha_Creacion = DateTime.Now,
                        IdFicha = ficha.IdFicha,
                        Id_Asignacion = primeraAsignacionId,
                        IdInstructorLider = idInstructorLider
                    });
                }
                else
                {
                    horarioExistente.Id_Asignacion = primeraAsignacionId;
                    horarioExistente.IdInstructorLider = idInstructorLider;
                    db.Entry(horarioExistente).State = EntityState.Modified;
                }

                db.SaveChanges();

                return Json(new { ok = true, msg = "✅ Horario y asignaciones guardadas correctamente." });
            }
            catch (Exception ex)
            {
                string real = ex.InnerException?.InnerException?.Message
                              ?? ex.InnerException?.Message
                              ?? ex.Message;

                return Json(new { ok = false, msg = "❌ Error real: " + real });
            }
        }









        // =================== HORARIOS POR FICHA ===================
        [HttpGet]
        public JsonResult GetHorariosFicha()
        {
            try
            {
                var data = (
                    from h in db.Horario
                    join f in db.Ficha on h.IdFicha equals f.IdFicha
                    join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma

                    // 🔥 FORZAMOS a EF a incluir IdInstructorLider
                    join i in db.Instructor
                         on (h.IdInstructorLider ?? 0)
                         equals i.IdInstructor
                         into liderJoin

                    from lider in liderJoin.DefaultIfEmpty() // left join

                    orderby h.Fecha_Creacion descending

                    select new
                    {
                        h.Id_Horario,
                        h.Año_Horario,
                        h.Trimestre_Año,
                        h.Fecha_Creacion,
                        f.IdFicha,
                        f.CodigoFicha,
                        ProgramaNombre = p.DenominacionPrograma,

                        // 🔥 AQUÍ SE CORRIGE MOSTRADO DEL INSTRUCTOR
                        InstructorLider = lider != null
                            ? lider.NombreCompletoInstructor
                            : "Sin asignar"
                    }
                ).ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }



        // =================== HORARIOS POR INSTRUCTOR ===================
        [HttpGet]
        public JsonResult GetHorariosInstructor()
        {
            try
            {
                var data = (from hi in db.HorarioInstructor
                            join i in db.Instructor on hi.IdInstructor equals i.IdInstructor
                            join f in db.Ficha on hi.IdFicha equals f.IdFicha
                            orderby hi.IdFicha, hi.Dia
                            select new
                            {
                                hi.IdHorarioInstructor,
                                hi.IdFicha,
                                f.CodigoFicha,
                                IdInstructor = hi.IdInstructor,
                                NombreInstructor = i.NombreCompletoInstructor,
                                hi.Competencia,
                                hi.Resultado,
                                hi.Dia,

                                // 🔥 Asegura formato HH:mm
                                HoraDesde = hi.HoraDesde.ToString(@"hh\:mm"),
                                HoraHasta = hi.HoraHasta.ToString(@"hh\:mm")
                            }).ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        // =================== DETALLE POR FICHA ===================
        [HttpGet]
        public JsonResult GetDetalleFicha(int idFicha)
        {
            try
            {
                var data = (from hi in db.HorarioInstructor
                            join i in db.Instructor on hi.IdInstructor equals i.IdInstructor
                            join f in db.Ficha on hi.IdFicha equals f.IdFicha
                            where hi.IdFicha == idFicha
                            orderby hi.Dia
                            select new
                            {
                                hi.IdHorarioInstructor,
                                f.CodigoFicha,
                                NombreInstructor = i.NombreCompletoInstructor,
                                hi.Competencia,
                                hi.Resultado,
                                hi.Dia,
                                HoraDesde = hi.HoraDesde.ToString().Substring(0, 5),
                                HoraHasta = hi.HoraHasta.ToString().Substring(0, 5)
                            }).ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }



        public ActionResult VerHorarioFicha(int idFicha)
        {
            var horarios = db.HorarioInstructor
                .Where(h => h.IdFicha == idFicha)
                .Include("Instructor")
                .Include("Ficha")
                .ToList();

            var ficha = db.Ficha.FirstOrDefault(f => f.IdFicha == idFicha);
            ViewBag.CodigoFicha = ficha?.CodigoFicha?.ToString() ?? "N/A";
            ViewBag.TrimestreActual = ficha?.Trimestre ?? 1; // ✅ agregado

            return View(horarios);
        }

        [HttpGet]
        public JsonResult GetHorariosInstructores()
        {
            try
            {
                var data = db.HorarioInstructor
                    .Include("Instructor")
                    .GroupBy(h => new
                    {
                        h.IdInstructor,
                        h.Instructor.NombreCompletoInstructor
                    })
                    .Select(g => new
                    {
                        IdInstructor = g.Key.IdInstructor,
                        NombreInstructor = g.Key.NombreCompletoInstructor
                    })
                    .OrderBy(x => x.NombreInstructor)
                    .ToList();

                return Json(new { ok = true, data = data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }


        public ActionResult VerHorarioInstructor(int idInstructor)
        {
            var horarios = db.HorarioInstructor
                .Where(h => h.IdInstructor == idInstructor)
                .Include("Instructor")
                .Include("Ficha")
                .ToList();

            var instructor = db.Instructor.FirstOrDefault(i => i.IdInstructor == idInstructor);
            ViewBag.NombreInstructor = instructor?.NombreCompletoInstructor ?? "Instructor";

            return View("VerHorarioInstructor", horarios); // 👈 Forzamos a usar la vista correcta
        }




        public ActionResult CrearSiguienteHorario(int idFicha, int trimestre)
        {
            try
            {
                // 🔹 Calculamos el trimestre siguiente
                int trimestreSiguiente = (trimestre < 7) ? trimestre + 1 : 7;

                // 🔹 Obtenemos la ficha
                var ficha = db.Ficha
                    .Include("Programa_Formacion")
                    .FirstOrDefault(f => f.IdFicha == idFicha);

                if (ficha == null)
                    return HttpNotFound("Ficha no encontrada.");

                // 🔹 Cargamos los registros del diseño curricular asociados a la ficha
                var registros = db.Diseño_Curricular
                    .Where(c => c.IdFicha == idFicha)
                    .ToList();

                // 🔹 Filtramos por el trimestre siguiente directamente en la tabla
                var resultadosTrimestre = registros
                    .Where(r =>
                        (trimestreSiguiente == 1 && (r.HrTrimI ?? 0) > 0) ||
                        (trimestreSiguiente == 2 && (r.HrTrimII ?? 0) > 0) ||
                        (trimestreSiguiente == 3 && (r.HrTrimIII ?? 0) > 0) ||
                        (trimestreSiguiente == 4 && (r.HrTrimIV ?? 0) > 0) ||
                        (trimestreSiguiente == 5 && (r.HrTrimV ?? 0) > 0) ||
                        (trimestreSiguiente == 6 && (r.HrTrimVI ?? 0) > 0) ||
                        (trimestreSiguiente == 7 && (r.HrTrimVII ?? 0) > 0)
                    )
                    .OrderBy(r => r.Competencia)
                    .ThenBy(r => r.Resultado)
                    .ToList();

                // 🔹 Enviamos información de la ficha al ViewBag
                ViewBag.IdFicha = idFicha;
                ViewBag.CodigoFicha = ficha.CodigoFicha;
                ViewBag.Programa = ficha.Programa_Formacion?.DenominacionPrograma ?? "Sin programa";
                ViewBag.TrimestreActual = trimestre;
                ViewBag.TrimestreSiguiente = trimestreSiguiente;

                return View(resultadosTrimestre); // 👈 directamente la lista de Diseño_Curricular
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Error al cargar el siguiente horario: " + ex.Message;
                return View(new List<Diseño_Curricular>());
            }
        }



        private decimal CalcularHorasTrabajadas(TimeSpan desde, TimeSpan hasta)
        {
            var horas = (decimal)(hasta - desde).TotalHours;
            if (horas < 0) horas = 0;
            return horas;
        }



        [HttpGet]
        public JsonResult ValidarChoqueInstructorGlobal(
      int idInstructor,
      string dia,
      string desde,
      string hasta,
      int idFichaActual)
        {
            try
            {
                TimeSpan hDesde = TimeSpan.Parse(desde);
                TimeSpan hHasta = TimeSpan.Parse(hasta);

                bool choque = db.Asignacion_horario.Any(h =>
                    h.IdInstructor == idInstructor &&
                    h.IdFicha != idFichaActual &&
                    h.Dia == dia &&
                    (
                        (hDesde >= h.HoraDesde && hDesde < h.HoraHasta) ||
                        (hHasta > h.HoraDesde && hHasta <= h.HoraHasta) ||
                        (hDesde <= h.HoraDesde && hHasta >= h.HoraHasta)
                    )
                );

                return Json(new { ok = true, choque = choque }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }


        [HttpGet]
        public JsonResult GetAsignacionesInstructor()
        {
            try
            {
                var data = db.Asignacion_horario
                    .Select(a => new
                    {
                        IdInstructor = a.IdInstructor,
                        IdFicha = a.IdFicha,
                        Dia = a.Dia,
                        HoraDesde = a.HoraDesde.ToString(@"hh\:mm"),
                        HoraHasta = a.HoraHasta.ToString(@"hh\:mm")
                    })
                    .ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }




        [HttpGet]
        public JsonResult GetHorasInstructor(int idInstructor)
        {
            try
            {
                var inst = db.Instructor.FirstOrDefault(i => i.IdInstructor == idInstructor);

                if (inst == null)
                    return Json(new { ok = false, msg = "Instructor no encontrado." }, JsonRequestBehavior.AllowGet);

                return Json(new
                {
                    ok = true,
                    horasActuales = inst.Horas_Trabajadas ?? 0,
                    horasMaximas = inst.Horas_De_Trabajo ?? 0
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }








    }




}