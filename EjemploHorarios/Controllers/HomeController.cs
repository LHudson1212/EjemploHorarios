using EjemploHorarios.Models;
using EjemploHorarios.Models.ViewModels;
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

                // ✅ evita pisar archivos con mismo nombre
                string uniqueName = $"{Guid.NewGuid()}_{Path.GetFileName(archivoExcel.FileName)}";
                string filePath = Path.Combine(path, uniqueName);
                archivoExcel.SaveAs(filePath);

                var ficha = db.Ficha.Include("Programa_Formacion")
                                    .FirstOrDefault(f => f.IdFicha == idFicha);

                if (ficha == null)
                    return Json(new { ok = false, msg = "Ficha no encontrada." });

                int horarioId = ObtenerHorarioValido(idFicha, anio, trimestre);

                string programaNombre = ficha.Programa_Formacion?.DenominacionPrograma ?? "Programa desconocido";

                using (var package = new ExcelPackage(new FileInfo(filePath)))
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

        // 👈 Asegúrate de tener este using arriba
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
                // 2. Validar ficha
                // ============================
                var ficha = db.Ficha.FirstOrDefault(f => f.CodigoFicha.ToString() == numeroFicha);
                if (ficha == null)
                    return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                // ============================
                // 3. Validar trimestre
                // ============================
                int tri = int.TryParse(trimestre, out var parsedTri) ? parsedTri : 0;

                var horarioExistente = db.Horario
                    .FirstOrDefault(h => h.IdFicha == ficha.IdFicha && h.Trimestre_Año == tri);

                int? primeraAsignacionId = null;

                const int SEMANAS_TRIMESTRE = 12;

                var asignacionesEntidad = new List<Asignacion_horario>();
                var horariosInstructorEntidad = new List<HorarioInstructor>();


                    // ============================
                    // 4. Validación interna entre asignaciones nuevas (EVITA DUPLICADOS EN LA MISMA CARGA)
                    // ============================
                    foreach (var group in asignaciones
                        .Where(a => a.instructorId > 0)
                        .GroupBy(a => new { a.instructorId, a.dia }))
                    {
                        var lista = group.ToList();

                        for (int i = 0; i < lista.Count; i++)
                        {
                            for (int j = i + 1; j < lista.Count; j++)
                            {
                                TimeSpan d1 = TimeSpan.Parse(lista[i].horaDesde);
                                TimeSpan h1 = TimeSpan.Parse(lista[i].horaHasta);
                                TimeSpan d2 = TimeSpan.Parse(lista[j].horaDesde);
                                TimeSpan h2 = TimeSpan.Parse(lista[j].horaHasta);

                                bool internoChoque =
                                    (d1 >= d2 && d1 < h2) ||
                                    (h1 > d2 && h1 <= h2) ||
                                    (d1 <= d2 && h1 >= h2);

                                if (internoChoque)
                                {
                                    return Json(new
                                    {
                                        ok = false,
                                        msg = $"❌ El instructor ID {group.Key.instructorId} tiene un choque interno en el día {group.Key.dia}. " +
                                              $"Dos asignaciones nuevas tienen horas traslapadas."
                                    });
                                }
                            }
                        }
                    }

                // ============================
                // 5. Procesar asignaciones una por una
                // ============================
                foreach (var a in asignaciones)
                {
                    if (a.instructorId <= 0)
                        continue;

                    TimeSpan horaDesde = TimeSpan.Parse(a.horaDesde);
                    TimeSpan horaHasta = TimeSpan.Parse(a.horaHasta);

                    decimal horasPorDia = (decimal)(horaHasta - horaDesde).TotalHours;
                    if (horasPorDia < 0) horasPorDia = 0;

                    decimal horasAsignacion = Math.Round(horasPorDia * SEMANAS_TRIMESTRE, 2);

                    var instructor = db.Instructor.FirstOrDefault(i => i.IdInstructor == a.instructorId);
                    if (instructor == null) continue;

                    decimal horasActuales = instructor.Horas_Trabajadas ?? 0m;
                    decimal horasMaximas = instructor.Horas_De_Trabajo ?? 0m;
                    decimal nuevoTotal = horasActuales + horasAsignacion;


                    // ============================
                    // 6. Validar exceso de horas
                    // ============================
                    if (nuevoTotal > horasMaximas)
                    {
                        return Json(new
                        {
                            ok = false,
                            msg = $"❌ El instructor {instructor.NombreCompletoInstructor} supera su límite de horas. " +
                                  $"Máximo {horasMaximas}, actuales {horasActuales}, intento sumar {horasAsignacion}."
                        });
                    }


                    // ============================
                    // 7. Validar choque con BD
                    // ============================
                    bool choqueBD = db.HorarioInstructor.Any(h =>
                        h.IdInstructor == a.instructorId &&
                        h.IdFicha == ficha.IdFicha &&
                        h.Dia == a.dia &&
                        (
                            (horaDesde >= h.HoraDesde && horaDesde < h.HoraHasta) ||
                            (horaHasta > h.HoraDesde && horaHasta <= h.HoraHasta) ||
                            (horaDesde <= h.HoraDesde && horaHasta >= h.HoraHasta)
                        )
                    );

                    if (choqueBD)
                    {
                        return Json(new
                        {
                            ok = false,
                            msg = $"❌ Choque detectado: {instructor.NombreCompletoInstructor} ya tiene clase " +
                                  $"el {a.dia} entre {horaDesde} y {horaHasta}."
                        });
                    }


                    // ============================
                    // 8. Actualizar horas trabajadas
                    // ============================
                    instructor.Horas_Trabajadas = Math.Round(nuevoTotal, 2);
                    db.Entry(instructor).State = EntityState.Modified;


                    // ============================
                    // 9. Preparar asignación
                    // ============================
                    var asignacion = new Asignacion_horario
                    {
                        Dia = a.dia,
                        HoraDesde = horaDesde,
                        HoraHasta = horaHasta,
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha
                    };
                    asignacionesEntidad.Add(asignacion);


                    // ============================
                    // 10. Sanitizar textos
                    // ============================
                    string comp = (a.competencia ?? "Pendiente");
                    string res = (a.resultado ?? "Pendiente");

                    if (comp.Length > 250) comp = comp.Substring(0, 250);
                    if (res.Length > 2000) res = res.Substring(0, 2000);

                    horariosInstructorEntidad.Add(new HorarioInstructor
                    {
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha,
                        Competencia = comp,
                        Resultado = res,
                        Dia = a.dia,
                        HoraDesde = horaDesde,
                        HoraHasta = horaHasta
                    });
                }


                // ============================
                // 11. Guardar asignaciones
                // ============================
                foreach (var asig in asignacionesEntidad)
                    db.Asignacion_horario.Add(asig);

                foreach (var hi in horariosInstructorEntidad)
                    db.HorarioInstructor.Add(hi);

                db.SaveChanges();

                primeraAsignacionId = asignacionesEntidad.First().Id_Asignacion;


                // ============================
                // 12. Crear o actualizar horario
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
                string errorReal = ex.InnerException?.InnerException?.Message ??
                                   ex.InnerException?.Message ??
                                   ex.Message;

                return Json(new { ok = false, msg = "❌ Error real: " + errorReal });
            }
        }


        // =================== HORARIOS POR FICHA (corregido con TrimestreFicha y HorarioTrimestre) ===================
        [HttpGet]
        public JsonResult GetHorariosFicha(string programa = "", string codigoFicha = "",
                                   int? trimestreAno = null, int? trimestreFicha = null,
                                   int? idInstructorLider = null)
        {
            try
            {
                // 1) Traemos datos crudos desde la BD (sin ToString ni formatos)
                var raw = (
                    from h in db.Horario
                    join f in db.Ficha on h.IdFicha equals f.IdFicha
                    join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma
                    join i in db.Instructor on h.IdInstructorLider equals (int?)i.IdInstructor into liderJoin
                    from lider in liderJoin.DefaultIfEmpty()
                    orderby h.Fecha_Creacion descending
                    select new
                    {
                        h.Id_Horario,
                        h.Año_Horario,
                        Trimestre_Año = h.Trimestre_Año,    // valor guardado en Horario (1..4)
                        h.Fecha_Creacion,
                        f.IdFicha,
                        CodigoFicha = f.CodigoFicha,
                        ProgramaNombre = p.DenominacionPrograma,
                        FechaInFicha = f.FechaInFicha,
                        FechaFinFicha = f.FechaFinFicha,
                        // Trimestre actual de la ficha (nullable)
                        FichaTrimestre = f.Trimestre,        // int?
                        InstructorLiderId = h.IdInstructorLider, // int?
                        InstructorLiderNombre = lider != null ? lider.NombreCompletoInstructor : null
                    }
                ).ToList(); // ejecuta la consulta en la BD aquí

                // 2) Aplicamos filtros en memoria
                var filtered = raw.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(programa))
                {
                    var pterm = programa.Trim();
                    filtered = filtered.Where(x => (x.ProgramaNombre ?? "")
                        .IndexOf(pterm, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (!string.IsNullOrWhiteSpace(codigoFicha))
                {
                    var s = codigoFicha.Trim();
                    filtered = filtered.Where(x => (x.CodigoFicha != null ? x.CodigoFicha.ToString() : "")
                        .IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (trimestreAno.HasValue)
                {
                    // Trimestre del AÑO (valor guardado en Horario.Trimestre_Año) — típicamente 1..4
                    filtered = filtered.Where(x => x.Trimestre_Año == trimestreAno.Value);
                }

                if (trimestreFicha.HasValue)
                {
                    // Trimestre al que va la ficha: Ficha.Trimestre + 1 (tope 7)
                    // x.FichaTrimestre es int? -> usar (x.FichaTrimestre ?? 1)
                    filtered = filtered.Where(x =>
                    {
                        int fichaTri = x.FichaTrimestre ?? 1;
                        int triFichaCalc = fichaTri >= 7 ? 7 : fichaTri + 1;
                        return triFichaCalc == trimestreFicha.Value;
                    });
                }

                if (idInstructorLider.HasValue)
                {
                    // InstructorLiderId es int? por eso usamos ?? 0
                    filtered = filtered.Where(x => (x.InstructorLiderId ?? 0) == idInstructorLider.Value);
                }

                // 3) Proyección / formateo final
                var data = filtered.Select(x => new
                {
                    x.Id_Horario,
                    x.Año_Horario,
                    Trimestre_Año = x.Trimestre_Año,
                    x.Fecha_Creacion,
                    x.IdFicha,
                    CodigoFicha = x.CodigoFicha != null ? x.CodigoFicha.ToString() : "",
                    ProgramaNombre = x.ProgramaNombre ?? "",
                    FechaInicioFicha = x.FechaInFicha.HasValue ? x.FechaInFicha.Value.ToString("dd/MM/yyyy") : "",
                    FechaFinFicha = x.FechaFinFicha.HasValue ? x.FechaFinFicha.Value.ToString("dd/MM/yyyy") : "",
                    // Trimestre al que va la ficha (1..7)
                    TrimestreFicha = ((x.FichaTrimestre ?? 1) >= 7) ? 7 : (x.FichaTrimestre ?? 1) + 1,
                    // Trimestre del año (el valor guardado en Horario, 1..4)
                    TrimestreDelAnio = x.Trimestre_Año,
                    InstructorLider = string.IsNullOrWhiteSpace(x.InstructorLiderNombre) ? "Sin asignar" : x.InstructorLiderNombre,
                    InstructorLiderId = x.InstructorLiderId ?? 0
                }).ToList();

                return Json(new { ok = true, count = data.Count, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("GetHorariosFicha error: " + ex.ToString());
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        // =================== HORARIOS POR INSTRUCTOR ===================
        [HttpGet]
        public JsonResult GetHorariosInstructor()
        {
            try
            {
                // 1) Traer en bruto desde BD (sin ToString o formatos)
                var raw = (from hi in db.HorarioInstructor
                           join i in db.Instructor on hi.IdInstructor equals i.IdInstructor
                           join f in db.Ficha on hi.IdFicha equals f.IdFicha
                           orderby hi.IdFicha, hi.Dia
                           select new
                           {
                               hi.IdHorarioInstructor,
                               hi.IdFicha,
                               CodigoFicha = f.CodigoFicha,
                               IdInstructor = hi.IdInstructor,
                               NombreInstructor = i.NombreCompletoInstructor,
                               hi.Competencia,
                               hi.Resultado,
                               hi.Dia,
                               HoraDesde = hi.HoraDesde, // TimeSpan?
                               HoraHasta = hi.HoraHasta  // TimeSpan?
                           }).ToList(); // Ejecuta en la BD

                // 2) Formatear en memoria
                var data = raw.Select(x => new
                {
                    x.IdHorarioInstructor,
                    x.IdFicha,
                    CodigoFicha = x.CodigoFicha != null ? x.CodigoFicha.ToString() : "",
                    x.IdInstructor,
                    NombreInstructor = x.NombreInstructor ?? "(Sin nombre)",
                    Competencia = x.Competencia ?? "",
                    Resultado = x.Resultado ?? "",
                    x.Dia,
                    HoraDesde = x.HoraDesde != null ? x.HoraDesde.ToString(@"hh\:mm") : "--:--",
                    HoraHasta = x.HoraHasta != null ? x.HoraHasta.ToString(@"hh\:mm") : "--:--"
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
                var raw = (from hi in db.HorarioInstructor
                           join i in db.Instructor on hi.IdInstructor equals i.IdInstructor
                           join f in db.Ficha on hi.IdFicha equals f.IdFicha
                           where hi.IdFicha == idFicha
                           orderby hi.Dia
                           select new
                           {
                               hi.IdHorarioInstructor,
                               CodigoFicha = f.CodigoFicha,
                               NombreInstructor = i.NombreCompletoInstructor,
                               hi.Competencia,
                               hi.Resultado,
                               hi.Dia,
                               HoraDesde = hi.HoraDesde,
                               HoraHasta = hi.HoraHasta
                           }).ToList();

                var data = raw.Select(x => new
                {
                    x.IdHorarioInstructor,
                    CodigoFicha = x.CodigoFicha != null ? x.CodigoFicha.ToString() : "",
                    NombreInstructor = x.NombreInstructor ?? "",
                    Competencia = x.Competencia ?? "",
                    Resultado = x.Resultado ?? "",
                    x.Dia,
                    HoraDesde = x.HoraDesde != null ? x.HoraDesde.ToString(@"hh\:mm") : "--:--",
                    HoraHasta = x.HoraHasta != null ? x.HoraHasta.ToString(@"hh\:mm") : "--:--"
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
        public JsonResult ValidarChoqueHorarioBD(
        int idInstructor,
        int idFicha,
        string dia,
        string desde,
        string hasta)
        {
            try
            {
                TimeSpan horaDesde = TimeSpan.Parse(desde);
                TimeSpan horaHasta = TimeSpan.Parse(hasta);

                // misma lógica que usas en GuardarHorario
                bool choqueBD = db.HorarioInstructor.Any(h =>
                    h.IdInstructor == idInstructor &&
                    h.IdFicha == idFicha &&
                    h.Dia == dia &&
                    (
                        (horaDesde >= h.HoraDesde && horaDesde < h.HoraHasta) ||
                        (horaHasta > h.HoraDesde && horaHasta <= h.HoraHasta) ||
                        (horaDesde <= h.HoraDesde && horaHasta >= h.HoraHasta)
                    )
                );

                if (choqueBD)
                {
                    var instructor = db.Instructor.FirstOrDefault(i => i.IdInstructor == idInstructor);
                    var nombre = instructor?.NombreCompletoInstructor ?? $"ID {idInstructor}";

                    return Json(new
                    {
                        ok = false,
                        msg = $"❌ Choque detectado: {nombre} ya tiene clase el {dia} entre {horaDesde} y {horaHasta}."
                    }, JsonRequestBehavior.AllowGet);
                }

                return Json(new { ok = true }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    msg = "Error validando horario en BD: " + ex.Message
                }, JsonRequestBehavior.AllowGet);
            }
        }
        

        [HttpGet]
        public JsonResult GetFichaResumen(int idFicha)
        {
            try
            {
                var ficha = db.Ficha
                              .Include("Programa_Formacion")
                              .FirstOrDefault(f => f.IdFicha == idFicha);

                if (ficha == null)
                    return Json(new { ok = false, msg = "Ficha no encontrada." }, JsonRequestBehavior.AllowGet);

                int triActual = ficha.Trimestre ?? 1;
                int triDestino = (triActual < 7) ? triActual + 1 : 7;

                var programaNombre = ficha.Programa_Formacion?.DenominacionPrograma ?? "Sin programa";

                return Json(new
                {
                    ok = true,
                    data = new
                    {
                        ProgramaNombre = programaNombre,
                        CodigoFicha = ficha.CodigoFicha?.ToString() ?? "",
                        TrimestreDestino = triDestino,
                        FechaInicio = ficha.FechaInFicha?.ToString("yyyy-MM-dd") ?? "",
                        FechaFin = ficha.FechaFinFicha?.ToString("yyyy-MM-dd") ?? ""
                    }
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "Error: " + ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpGet]
        public JsonResult ValidarFranjaExactaInstructor(int idInstructor, string dia, string desde, string hasta, int idFichaActual)
        {
            try
            {
                TimeSpan hDesde = TimeSpan.Parse(desde);
                TimeSpan hHasta = TimeSpan.Parse(hasta);

                // Buscamos una franja EXACTA en HorarioInstructor para el mismo instructor y día (en la misma ficha podría ignorarse)
                bool choque = db.HorarioInstructor.Any(h =>
                    h.IdInstructor == idInstructor &&
                    h.Dia == dia &&
                    h.IdFicha != idFichaActual &&
                    h.HoraDesde == hDesde &&
                    h.HoraHasta == hHasta
                );

                return Json(new { ok = true, choque = choque }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }

        [HttpGet]
        public JsonResult GetProgramas()
        {
            try
            {
                var data = db.Programa_Formacion
                             .OrderBy(p => p.DenominacionPrograma)
                             .Select(p => p.DenominacionPrograma)
                             .Distinct()
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