using EjemploHorarios.Models;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
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

        // ========== FILTRO DE FICHAS DESDE INDEX ==========
        // Devuelve SOLO fichas en ejecución (EstadoFicha = 1) que están activas en el trimestre seleccionado
        // Además incluye IdPrograma y DenominacionPrograma (ProgramaNombre) para autoseleccionar el programa en la vista.
        [HttpGet]
        public JsonResult GetFichasEnFormacion(int anio, int trimestre)
        {
            try
            {
                if (anio <= 0 || trimestre < 1 || trimestre > 4)
                    return Json(new { ok = false, msg = "Parámetros inválidos." }, JsonRequestBehavior.AllowGet);

                // Calcular el rango de fechas del trimestre
                var inicioTrimestre = new DateTime(anio, ((trimestre - 1) * 3) + 1, 1);
                var finTrimestre = inicioTrimestre.AddMonths(3).AddDays(-1);

                // Buscar fichas vigentes durante ese rango de trimestre
                var fichas = (from f in db.Ficha
                              join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma
                              where f.FechaInFicha.HasValue
                                    && f.FechaFinFicha.HasValue
                                    && f.FechaInFicha.Value <= finTrimestre   // empezó antes o dentro del trimestre
                                    && f.FechaFinFicha.Value >= inicioTrimestre // termina después o dentro del trimestre
                              select new
                              {
                                  f.IdFicha,
                                  f.CodigoFicha,
                                  f.IdPrograma,
                                  ProgramaNombre = p.DenominacionPrograma,
                                  TrimestreDeLaFicha = f.Trimestre,
                                  FechaInicio = f.FechaInFicha,
                                  FechaFin = f.FechaFinFicha
                              })
                              .OrderBy(f => f.CodigoFicha)
                              .ToList();

                if (!fichas.Any())
                    return Json(new { ok = false, msg = "No hay fichas vigentes para el año y trimestre seleccionados." },
                                JsonRequestBehavior.AllowGet);

                return Json(new { ok = true, data = fichas }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "Error al obtener las fichas: " + ex.Message },
                            JsonRequestBehavior.AllowGet);
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

            var data = FiltrarCompetenciasPorTrimestre(idFicha, trimestre);
            if (!data.Any())
                return Json(new { ok = false, msg = "No hay competencias para este trimestre." }, JsonRequestBehavior.AllowGet);

            return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
        }
        private List<CompetenciaDTO> FiltrarCompetenciasPorTrimestre(int idFicha, int trimestreFicha)
        {
            // 🔹 Calcula el trimestre siguiente
            int trimestreObjetivo = (trimestreFicha < 7) ? trimestreFicha + 1 : 7;

            // 🔹 Obtiene solo los registros de la ficha
            var registros = db.Diseño_Curricular
                .Where(c => c.IdFicha == idFicha)
                .ToList();

            // 🔹 Agrupa por competencia y toma solo los resultados del trimestre siguiente
            var competencias = registros
                .GroupBy(c => c.Competencia)
                .Select(g => new CompetenciaDTO
                {
                    Competencia = g.Key,
                    Resultados = g
                        .Where(r =>
                            (trimestreObjetivo == 1 && (r.HrTrimI ?? 0) > 0) ||
                            (trimestreObjetivo == 2 && (r.HrTrimII ?? 0) > 0) ||
                            (trimestreObjetivo == 3 && (r.HrTrimIII ?? 0) > 0) ||
                            (trimestreObjetivo == 4 && (r.HrTrimIV ?? 0) > 0) ||
                            (trimestreObjetivo == 5 && (r.HrTrimV ?? 0) > 0) ||
                            (trimestreObjetivo == 6 && (r.HrTrimVI ?? 0) > 0) ||
                            (trimestreObjetivo == 7 && (r.HrTrimVII ?? 0) > 0)
                        )
                        .Select(r => r.Resultado)
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Distinct()
                        .ToList()
                })
                // 🔹 Filtra competencias que sí tienen resultados válidos
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
            var horario = db.Horario.FirstOrDefault(h => h.IdFicha == idFicha && h.Trimestre_Año == trimestre);
            if (horario != null)
                return horario.Id_Horario;

            // ✅ Si no existe, primero crear una Asignación válida (no vacía)
            var asignacion = db.Asignacion_horario.FirstOrDefault();
            if (asignacion == null)
            {
                // Puedes crear una "asignación base" genérica
                asignacion = new Asignacion_horario
                {
                    Dia = "Pendiente",
                    HoraDesde = new TimeSpan(6, 0, 0),  // 06:00 AM
                    HoraHasta = new TimeSpan(8, 0, 0),  // 08:00 AM
                    IdInstructor = db.Instructor.FirstOrDefault()?.IdInstructor ?? 1 // usa el primer instructor existente o 1
                };

                db.Asignacion_horario.Add(asignacion);
                db.SaveChanges();
            }

            // ✅ Luego crear el horario con referencia a esa asignación
            var nuevo = new Horario
            {
                IdFicha = idFicha,
                Año_Horario = anio,
                Trimestre_Año = trimestre,
                Fecha_Creacion = DateTime.Now,
                Id_Asignacion = asignacion.Id_Asignacion
            };

            db.Horario.Add(nuevo);
            db.SaveChanges();
            return nuevo.Id_Horario;
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

    }
}
