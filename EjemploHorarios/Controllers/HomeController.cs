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

                // ============================
                // 1. Guardar archivo temporal
                // ============================
                string path = Server.MapPath("~/Uploads/");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);

                string filePath = Path.Combine(path, Path.GetFileName(archivoExcel.FileName));
                archivoExcel.SaveAs(filePath);

                // ============================
                // 2. Buscar ficha
                // ============================
                var ficha = db.Ficha
                              .Include("Programa_Formacion")
                              .FirstOrDefault(f => f.IdFicha == idFicha);

                if (ficha == null)
                    return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                // ============================
                // ❌ 3. ELIMINAR HORARIOS TEMPORALES QUE BLOQUEAN TODO
                // ============================
                var temporales = db.Horario
                    .Where(h => h.IdFicha == idFicha &&
                                h.Trimestre_Año == trimestre &&
                                h.Id_Asignacion == null)   // ← TEMPORAL detectado
                    .ToList();

                if (temporales.Any())
                {
                    db.Horario.RemoveRange(temporales);
                    db.SaveChanges();
                }

                string programaNombre = ficha.Programa_Formacion?.DenominacionPrograma ?? "Programa desconocido";

                // ============================
                // 4. Procesar Excel
                // ============================
                using (var package = new OfficeOpenXml.ExcelPackage(new FileInfo(filePath)))
                {
                    var ws = package.Workbook.Worksheets["Hoja1"];
                    if (ws == null)
                        return Json(new { ok = false, msg = "❌ No se encontró la hoja 'Hoja1'." });

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
                            IdFicha = idFicha
                            // 👈 YA NO necesitamos Id_Horario AQUÍ
                        };

                        db.Diseño_Curricular.Add(registro);
                    }

                    db.SaveChanges();
                }

                // ============================
                // 5. Calcular trimestre DESTINO (académico 1–7)
                // ============================
                int trimestreActualFicha = ficha.Trimestre ?? 1;
                int trimestreDestino = trimestreActualFicha >= 7
                    ? 7
                    : trimestreActualFicha + 1;

                // ============================
                // 6. Filtrar resultados del TRIMESTRE DESTINO
                // ============================
                var competenciasFiltradas = FiltrarCompetenciasPorTrimestre(idFicha, trimestreDestino);

                return Json(new
                {
                    ok = true,
                    msg = "✅ Competencias cargadas correctamente. Ahora puedes guardar el horario.",
                    trimestreDestino = trimestreDestino,
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

            // ✔ AHORA usamos EXACTAMENTE el trimestre solicitado
            var data = FiltrarCompetenciasPorTrimestre(idFicha, trimestre);

            if (!data.Any())
                return Json(new
                {
                    ok = false,
                    msg = $"No hay resultados de aprendizaje para el trimestre {trimestre}."
                }, JsonRequestBehavior.AllowGet);

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
  string trimestreFicha,
  string trimestreAnio,
  int idInstructorLider)
        {
            try
            {
                var asignaciones = JsonConvert.DeserializeObject<List<AsignacionViewModel>>(AsignacionesJson);
                if (asignaciones == null || !asignaciones.Any())
                    return Json(new { ok = false, msg = "⚠️ No hay asignaciones para guardar." });

                var ficha = db.Ficha.FirstOrDefault(f => f.CodigoFicha.ToString() == numeroFicha);
                if (ficha == null)
                    return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                int trimestreActualFicha = ficha.Trimestre.GetValueOrDefault();
                int trimestreSolicitado = int.Parse(trimestreFicha);

                if (trimestreActualFicha >= 7)
                    return Json(new { ok = false, msg = "❌ La ficha ya está en trimestre 7." });

                if (trimestreSolicitado < trimestreActualFicha ||
                    trimestreSolicitado > (trimestreActualFicha + 1))
                    return Json(new { ok = false, msg = "❌ Trimestre inválido." });

                if (db.Horario.Any(h => h.IdFicha == ficha.IdFicha && h.Trimestre_Año == trimestreSolicitado))
                    return Json(new { ok = false, msg = "❌ Ya existe un horario para este trimestre." });

                // Crear horario
                var horarioNuevo = new Horario
                {
                    Año_Horario = int.Parse(trimestreAnio),
                    Trimestre_Año = trimestreSolicitado,
                    Fecha_Creacion = DateTime.Now,
                    IdFicha = ficha.IdFicha,
                    IdInstructorLider = idInstructorLider
                };

                db.Horario.Add(horarioNuevo);
                db.SaveChanges();

                const int SEMANAS = 12;
                var pendientes = new List<object>();

                foreach (var a in asignaciones)
                {
                    if (a.instructorId <= 0)
                        continue;

                    // SANEAR valores undefined
                    string comp = string.IsNullOrWhiteSpace(a.competencia) || a.competencia == "undefined" ? "" : a.competencia;
                    string res = string.IsNullOrWhiteSpace(a.resultado) || a.resultado == "undefined" ? "" : a.resultado;

                    TimeSpan d = TimeSpan.Parse(a.horaDesde);
                    TimeSpan h = TimeSpan.Parse(a.horaHasta);

                    if (d >= h)
                        return Json(new { ok = false, msg = "❌ Hora inicial no válida." });

                    // Buscar horas requeridas
                    var dc = db.Diseño_Curricular.FirstOrDefault(x =>
                        x.Competencia == comp &&
                        x.Resultado == res &&
                        x.IdFicha == ficha.IdFicha);

                    int horasRequeridas = dc?.Duracion ?? 0;

                    // Calcular horas programadas (12 semanas)
                    int horasProgramadas = (int)((h - d).TotalHours * SEMANAS);

                    // Guardar asignación (sin competencia/resultado)
                    db.Asignacion_horario.Add(new Asignacion_horario
                    {
                        Dia = a.dia,
                        HoraDesde = d,
                        HoraHasta = h,
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha,
                        HorasProgramadas = horasProgramadas,
                        HorasTotales = horasRequeridas
                    });

                    // Guardar en HorarioInstructor (AQUÍ SÍ)
                    db.HorarioInstructor.Add(new HorarioInstructor
                    {
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha,
                        Dia = a.dia,
                        HoraDesde = d,
                        HoraHasta = h,
                        Competencia = comp,
                        Resultado = res
                    });

                    // Guardar pendiente si no se cumplieron horas
                    if (horasProgramadas < horasRequeridas)
                    {
                        pendientes.Add(new
                        {
                            Competencia = comp,
                            Resultado = res,
                            HorasFaltantes = horasRequeridas - horasProgramadas
                        });
                    }
                }
                
                db.SaveChanges();

                // Guardar pendientes en Horario
                horarioNuevo.CompetenciasPendientes =
                    pendientes.Any() ? JsonConvert.SerializeObject(pendientes) : null;

                db.Entry(horarioNuevo).State = EntityState.Modified;
                db.SaveChanges();

                // Actualizar trimestre de ficha
                ficha.Trimestre = Math.Min(7, trimestreSolicitado);
                db.Entry(ficha).State = EntityState.Modified;
                db.SaveChanges();

                return Json(new { ok = true, msg = "✅ Horario creado correctamente." });
            }
            catch (Exception ex)
            {
                string real = ex.InnerException?.InnerException?.Message
                              ?? ex.InnerException?.Message
                              ?? ex.Message;

                return Json(new { ok = false, msg = "❌ Error: " + real });
            }
        }





        // =================== HORARIOS POR FICHA ===================
        [HttpGet]
        public JsonResult GetHorariosFicha()
        {
            try
            {
                var query =
                    from h in db.Horario
                    join f in db.Ficha on h.IdFicha equals f.IdFicha into lf
                    from f in lf.DefaultIfEmpty()
                    join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma into lp
                    from p in lp.DefaultIfEmpty()
                    join inst in db.Instructor on h.IdInstructorLider equals inst.IdInstructor into li
                    from inst in li.DefaultIfEmpty()
                    orderby h.Fecha_Creacion descending
                    select new
                    {
                        h.Id_Horario,
                        h.IdFicha,
                        CodigoFicha = f.CodigoFicha,
                        ProgramaNombre = p.DenominacionPrograma,

                        FechaInicio = f.FechaInFicha,
                        FechaFin = f.FechaFinFicha,

                        TrimestreFicha = f.Trimestre,       // Trimestre REAL de la ficha (1–7)
                        TrimestreAnio = h.Año_Horario,      // 🔥 ESTE ES EL TRIMESTRE DEL AÑO QUE QUIERES
                        TrimestreAcademico = h.Trimestre_Año,  // 1–7

                        FechaCreacion = h.Fecha_Creacion,
                        InstructorLider = inst.NombreCompletoInstructor
                    };


                // Formateo fuera de LINQ to Entities
                var data = query
      .AsEnumerable()
      .Select(x => new
      {
          x.Id_Horario,
          x.IdFicha,

          CodigoFicha = x.CodigoFicha?.ToString(),
          x.ProgramaNombre,

          FechaInicioFicha = x.FechaInicio?.ToString("yyyy-MM-dd"),
          FechaFinFicha = x.FechaFin?.ToString("yyyy-MM-dd"),

          TrimestreFicha = x.TrimestreFicha,     // 1–7
          Trimestre_Año = x.TrimestreAnio,       // 🔥 TRIMESTRE DEL AÑO (1–4)

          FechaCreacionHorario = x.FechaCreacion?.ToString("yyyy-MM-dd HH:mm"),

          InstructorLider = x.InstructorLider ?? "Sin asignar"
      })
      .ToList();


                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }






        // =================== HORARIOS POR INSTRUCTOR ===================
        [HttpGet]
        public JsonResult GetDetalleHorariosInstructor(int idInstructor)
        {
            try
            {
                var dataBD = db.Asignacion_horario
                    .Where(h => h.IdInstructor == idInstructor)
                    .Join(db.Ficha,
                        h => h.IdFicha,
                        f => f.IdFicha,
                        (h, f) => new
                        {
                            h.Dia,
                            h.HoraDesde,
                            h.HoraHasta,
                            f.CodigoFicha
                        })
                    .ToList();

                var data = dataBD.Select(x => new
                {
                    Dia = x.Dia,
                    HoraDesde = x.HoraDesde.ToString(@"hh\:mm"),
                    HoraHasta = x.HoraHasta.ToString(@"hh\:mm"),
                    CodigoFicha = x.CodigoFicha
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

//
        [HttpGet]
        public JsonResult GetHorariosInstructores(int idInstructor)
        {
            try
            {
                var dataBD = db.Asignacion_horario
                    .Where(h => h.IdInstructor == idInstructor)
                    .Join(db.Ficha,
                        h => h.IdFicha,
                        f => f.IdFicha,
                        (h, f) => new
                        {
                            h.Dia,
                            h.HoraDesde,
                            h.HoraHasta,
                            f.CodigoFicha
                        })
                    .ToList();   // ← Importante: ejecutar query ANTES de formatear

                var data = dataBD.Select(x => new
                {
                    Dia = x.Dia,
                    HoraDesde = x.HoraDesde.ToString(@"hh\:mm"),
                    HoraHasta = x.HoraHasta.ToString(@"hh\:mm"),
                    CodigoFicha = x.CodigoFicha
                }).ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }


        [HttpGet]
        public JsonResult GetHorariosInstructor()
        {
            try
            {
                // Buscamos instructores que tengan al menos una asignación
                var data = db.Asignacion_horario
                    .GroupBy(a => a.IdInstructor)
                    .Select(g => new
                    {
                        IdInstructor = g.Key,
                        NombreInstructor = db.Instructor
                            .Where(i => i.IdInstructor == g.Key)
                            .Select(i => i.NombreCompletoInstructor)
                            .FirstOrDefault()
                    })
                    .Where(x => x.NombreInstructor != null) // solo instructores válidos
                    .OrderBy(x => x.NombreInstructor)
                    .ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }


        [HttpGet]
        public JsonResult ValidarFranjaExactaInstructor(int idInstructor, string dia, string desde, string hasta, int idFichaActual = 0)
        {
            try
            {
                TimeSpan d = TimeSpan.Parse(desde);
                TimeSpan h = TimeSpan.Parse(hasta);

                var choque = db.Asignacion_horario
                    .Where(a =>
                        a.IdInstructor == idInstructor &&
                        a.Dia == dia &&
                        a.HoraDesde == d &&
                        a.HoraHasta == h &&
                        a.IdFicha != idFichaActual)
                    .Any();

                return Json(new { ok = true, choque }, JsonRequestBehavior.AllowGet);
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
        public JsonResult ValidarChoqueInstructorGlobal(int idInstructor,
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


        [HttpGet]
        public JsonResult GetResumenFicha(int idFicha)
        {
            try
            {
                var ficha = db.Ficha
                    .Include("Programa_Formacion")
                    .FirstOrDefault(f => f.IdFicha == idFicha);

                if (ficha == null)
                {
                    return Json(new { ok = false, msg = "Ficha no encontrada." },
                        JsonRequestBehavior.AllowGet);
                }

                return Json(new
                {
                    ok = true,
                    data = new
                    {
                        CodigoFicha = ficha.CodigoFicha.ToString(),
                        Programa = ficha.Programa_Formacion?.DenominacionPrograma ?? "",
                        FechaInicio = ficha.FechaInFicha?.ToString("yyyy-MM-dd") ?? "",
                        FechaFin = ficha.FechaFinFicha?.ToString("yyyy-MM-dd") ?? "",
                        TrimestreActual = ficha.Trimestre ?? 1
                    }
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message },
                    JsonRequestBehavior.AllowGet);
            }
        }






    }




}