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
                // 🔹 1. Actualiza automáticamente los estados antes de buscar
                ActualizarEstadosFichas();

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
                var hoy = DateTime.Now;

                var fichas = db.Ficha.ToList();

                foreach (var ficha in fichas)
                {
                    if (ficha.FechaFinFicha.HasValue)
                    {
                        DateTime fechaLimite = ficha.FechaFinFicha.Value.AddMonths(-6);

                        // Si la fecha actual supera la fecha fin - 6 meses → ya no puede tener horario lectivo
                        if (hoy > fechaLimite)
                            ficha.EstadoFicha = false; // práctica
                        else
                            ficha.EstadoFicha = true;  // lectiva
                    }
                }

                db.SaveChanges();
            }
            catch (Exception ex)
            {
                // Evita romper el flujo si algo falla (por ejemplo, bloqueo de DB)
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

                int horarioId = ObtenerHorarioValido(idFicha, anio, trimestre);
                string programaNombre = ficha.Programa_Formacion?.DenominacionPrograma ?? "Programa desconocido";

                var listaCompetencias = new List<CompetenciaDTO>();

                using (var package = new OfficeOpenXml.ExcelPackage(new FileInfo(filePath)))
                {
                    var ws = package.Workbook.Worksheets["Hoja1"];
                    if (ws == null)
                        return Json(new { ok = false, msg = "No se encontró la hoja 'Hoja1'." });

                    int rowCount = ws.Dimension.Rows;
                    string competenciaActual = null;

                    for (int row = 2; row <= rowCount; row++)
                    {
                        string competencia = ws.Cells[row, 4].Text?.Trim();   // Columna D
                        string resultado = ws.Cells[row, 6].Text?.Trim();     // Columna F
                        string instructorNombre = ws.Cells[row, 34].Text?.Trim(); // Columna AI

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

                // 🔹 Filtramos automáticamente por el trimestre de la ficha
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

        private int ObtenerInstructorId(string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre))
                nombre = "Instructor Genérico";

            var instructor = db.Instructor.FirstOrDefault(i => i.NombreCompletoInstructor == nombre);
            if (instructor != null) return instructor.IdInstructor;

            var nuevo = new Instructor { NombreCompletoInstructor = nombre, EstadoInstructor = true };
            db.Instructor.Add(nuevo);
            db.SaveChanges();
            return nuevo.IdInstructor;
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
                    System.Diagnostics.Debug.WriteLine("⚠️ Resultado vacío o nulo.");
                    return Json(new { ok = false, msg = "Resultado vacío." }, JsonRequestBehavior.AllowGet);
                }

                // 🔹 Normaliza el texto para evitar fallos por tildes o espacios
                string NormalizeText(string text)
                {
                    if (string.IsNullOrWhiteSpace(text)) return "";
                    var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
                    var sb = new System.Text.StringBuilder();
                    foreach (var c in normalized)
                    {
                        var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                        if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                            sb.Append(c);
                    }
                    return sb.ToString().ToUpperInvariant().Trim();
                }

                string resultadoNormalizado = NormalizeText(resultado);

                // 🔹 Busca coincidencia flexible en Diseño_Curricular
                var data = db.Diseño_Curricular
                    .AsEnumerable()
                    .FirstOrDefault(x =>
                    {
                        string res = NormalizeText(x.Resultado);
                        return res.Contains(resultadoNormalizado); // ← comparación más flexible
                    });

                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ No se encontró coincidencia en Diseño_Curricular para: {resultadoNormalizado}");

                    // 🔹 Devuelve Instructor Genérico si no hay coincidencia
                    var instructorDefault = db.Instructor.FirstOrDefault(i => i.IdInstructor == 1219)
                        ?? new Instructor
                        {
                            IdInstructor = 1219,
                            NombreCompletoInstructor = "Instructor Genérico",
                            EstadoInstructor = true
                        };

                    if (instructorDefault.IdInstructor == 0)
                    {
                        db.Instructor.Add(instructorDefault);
                        db.SaveChanges();
                        System.Diagnostics.Debug.WriteLine("🆕 Instructor genérico creado.");
                    }

                    return Json(new
                    {
                        ok = true,
                        data = new
                        {
                            IdInstructor = instructorDefault.IdInstructor,
                            Nombre = instructorDefault.NombreCompletoInstructor
                        }
                    }, JsonRequestBehavior.AllowGet);
                }

                // 🔹 Si se encuentra, devuelve el instructor real
                var instructor = db.Instructor.FirstOrDefault(i => i.IdInstructor == data.IdInstructor);
                System.Diagnostics.Debug.WriteLine($"✅ Instructor encontrado: {instructor?.NombreCompletoInstructor} (ID {data.IdInstructor})");

                return Json(new
                {
                    ok = true,
                    data = new
                    {
                        IdInstructor = instructor?.IdInstructor ?? 1219,
                        Nombre = instructor?.NombreCompletoInstructor ?? "Instructor Genérico"
                    }
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("💥 Error en GetInstructorPorResultado: " + ex.Message);
                return Json(new { ok = false, msg = "Error obteniendo el instructor: " + ex.Message },
                    JsonRequestBehavior.AllowGet);
            }
        }






        // 👈 Asegúrate de tener este using arriba
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult GuardarHorario(string AsignacionesJson, string numeroFicha, string nombreHorario, string trimestre)
        {
            try
            {
                // 🔹 Deserializar asignaciones
                var asignaciones = JsonConvert.DeserializeObject<List<AsignacionViewModel>>(AsignacionesJson);
                if (asignaciones == null || !asignaciones.Any())
                    return Json(new { ok = false, msg = "⚠️ No hay asignaciones para guardar." });

                // 🔹 Buscar ficha
                var ficha = db.Ficha.FirstOrDefault(f => f.CodigoFicha.ToString() == numeroFicha);
                if (ficha == null)
                    return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                // 🔹 Obtener trimestre numérico
                int tri = int.TryParse(trimestre, out var parsedTri) ? parsedTri : 0;

                // 🔹 Verificar si ya existe un horario para esa ficha y trimestre
                var horarioExistente = db.Horario
                    .FirstOrDefault(h => h.IdFicha == ficha.IdFicha && h.Trimestre_Año == tri);

                int? primeraAsignacionId = null;

                // 🔹 Crear asignaciones
                foreach (var a in asignaciones)
                {
                    if (a.instructorId <= 0) continue;

                    var asignacion = new Asignacion_horario
                    {
                        Dia = string.IsNullOrEmpty(a.dia) ? "Pendiente" : a.dia,
                        HoraDesde = TimeSpan.TryParse(a.horaDesde, out var desde) ? desde : new TimeSpan(6, 0, 0),
                        HoraHasta = TimeSpan.TryParse(a.horaHasta, out var hasta) ? hasta : new TimeSpan(9, 0, 0),
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha
                    };

                    db.Asignacion_horario.Add(asignacion);
                    db.SaveChanges();

                    if (primeraAsignacionId == null)
                        primeraAsignacionId = asignacion.Id_Asignacion;

                    // Guardar en HorarioInstructor
                    var horarioInstructor = new HorarioInstructor
                    {
                        IdInstructor = a.instructorId,
                        IdFicha = ficha.IdFicha,
                        Competencia = string.IsNullOrEmpty(a.competencia) ? "Pendiente" : a.competencia,
                        Resultado = string.IsNullOrEmpty(a.resultado) ? "Pendiente" : a.resultado,
                        Dia = string.IsNullOrEmpty(a.dia) ? "Pendiente" : a.dia,
                        HoraDesde = asignacion.HoraDesde,
                        HoraHasta = asignacion.HoraHasta
                    };
                    db.HorarioInstructor.Add(horarioInstructor);
                }

                // 🔹 Solo crear horario si no existe
                if (horarioExistente == null)
                {
                    var nuevoHorario = new Horario
                    {
                        Año_Horario = DateTime.Now.Year,
                        Trimestre_Año = tri,
                        Fecha_Creacion = DateTime.Now,
                        IdFicha = ficha.IdFicha,
                        Id_Asignacion = primeraAsignacionId
                    };
                    db.Horario.Add(nuevoHorario);
                }
                else
                {
                    // Si ya existe, solo actualiza la asignación principal
                    horarioExistente.Id_Asignacion = primeraAsignacionId;
                    db.Entry(horarioExistente).State = EntityState.Modified;
                }

                db.SaveChanges();

                return Json(new { ok = true, msg = "✅ Horario y asignaciones guardadas correctamente." });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "❌ Error al guardar el horario: " + ex.Message });
            }
        }





        // =================== HORARIOS POR FICHA ===================
        [HttpGet]
        public JsonResult GetHorariosFicha()
        {
            try
            {
                var data = (from h in db.Horario
                            join f in db.Ficha on h.IdFicha equals f.IdFicha
                            join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma
                            orderby h.Fecha_Creacion descending
                            select new
                            {
                                h.Id_Horario,
                                h.Año_Horario,
                                h.Trimestre_Año,
                                h.Fecha_Creacion,
                                f.IdFicha,
                                f.CodigoFicha,
                                ProgramaNombre = p.DenominacionPrograma
                            }).ToList();

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
            var data = db.HorarioInstructor
                .Include("Instructor")
                .Include("Ficha")
                .Select(h => new
                {
                    IdInstructor = h.IdInstructor, // 👈 Este campo es CLAVE
                    NombreInstructor = h.Instructor.NombreCompletoInstructor, // o h.Instructor.Nombre
                    CodigoFicha = h.Ficha.CodigoFicha,
                    Competencia = h.Competencia,
                    Resultado = h.Resultado,
                    Dia = h.Dia,
                    HoraDesde = h.HoraDesde.ToString(),
                    HoraHasta = h.HoraHasta.ToString()
                })
                .ToList();

            return Json(new { ok = true, data = data }, JsonRequestBehavior.AllowGet);
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

    }




}