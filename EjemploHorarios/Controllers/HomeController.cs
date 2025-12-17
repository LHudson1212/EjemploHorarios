using EjemploHorarios.Models;
using System.Data.Entity.Validation;
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
using Newtonsoft.Json.Linq;

namespace EjemploHorarios.Controllers
{
    public class HomeController : Controller
    {
        private readonly SenaPlanningEntities1 db = new SenaPlanningEntities1();

        // GET: Home/Index
        public ActionResult Index(int? idFicha = null, int? anio = null, int? trimestreDestino = null)
        {
            ActualizarEstadosFichas();

            ViewBag.AutoIdFicha = idFicha;
            ViewBag.AutoAnio = anio;
            ViewBag.AutoTrimestreDestino = trimestreDestino;

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
                term = (term ?? "").Trim();

                if (anio <= 0 || trimestre < 1 || trimestre > 4)
                {
                    return Json(
                        new { ok = false, msg = "Parámetros inválidos. Debes indicar año y trimestre (1–4)." },
                        JsonRequestBehavior.AllowGet
                    );
                }

                // 1) Rango del trimestre del AÑO seleccionado
                var rango = GetRangoTrimestreAnio(anio, trimestre);
                var inicioTrimestreAnio = rango.Inicio;

                // 2) Traer fichas válidas
                var fichasBD = db.Ficha
                    .Include(f => f.Programa_Formacion)
                    .Where(f => f.FechaInFicha.HasValue && f.FechaFinFicha.HasValue)
                    .Where(f => f.EstadoFicha) // si EstadoFicha es bool
                    .ToList(); // a memoria

                // 3) Traer en una sola consulta los horarios del AÑO seleccionado
                //    y armar una llave rápida IdFicha|TrimestreAcad
                var horariosDelAnio = db.Horario
                    .AsNoTracking()
                    .Where(h => h.Año_Horario == anio)
                    .Select(h => new { h.IdFicha, h.Trimestre_Año })
                    .ToList();

                var setHorarios = new HashSet<string>(
                    horariosDelAnio.Select(h => $"{h.IdFicha}|{h.Trimestre_Año}")
                );

                // 4) Proyección + filtro por regla de 180 días + excluir si ya existe horario para ese año/trimestreAcad
                var fichasFiltradas = fichasBD
                    .Select(f =>
                    {
                        var fechaInicio = f.FechaInFicha.Value.Date;
                        var fechaFin = f.FechaFinFicha.Value.Date;
                        var limiteProgramacion = fechaFin.AddDays(-180);

                        bool puedeProgramarseEnEsteTrimestre =
                            inicioTrimestreAnio >= fechaInicio &&
                            inicioTrimestreAnio <= limiteProgramacion;

                        // ✅ trimestre REAL en BD (1–7)
                        int trimestreFicha = f.Trimestre ?? 1;

                        // ✅ si ya existe horario en ese año y ese trimestre académico → NO debe aparecer

                        bool yaTieneHorario = setHorarios.Contains($"{f.IdFicha}|{trimestreFicha}");


                        return new
                        {
                            f.IdFicha,
                            f.CodigoFicha,
                            f.IdPrograma,
                            ProgramaNombre = f.Programa_Formacion?.DenominacionPrograma ?? "",
                            TrimestreDeLaFicha = trimestreFicha,
                            TrimestreCalculado = CalcularTrimestreFicha(fechaInicio, inicioTrimestreAnio),
                            FechaInicio = f.FechaInFicha,
                            FechaFin = f.FechaFinFicha,
                            PuedeProgramarse = puedeProgramarseEnEsteTrimestre && !yaTieneHorario
                        };
                    })
                    .Where(x => x.PuedeProgramarse)
                    .Where(x => x.TrimestreDeLaFicha > 0 && x.TrimestreDeLaFicha < 7)
                    .ToList();

                // 5) Filtro por término
                if (!string.IsNullOrEmpty(term))
                {
                    var low = term.ToLowerInvariant();
                    fichasFiltradas = fichasFiltradas
                        .Where(f =>
                            (f.CodigoFicha != null && f.CodigoFicha.ToString().Contains(term)) ||
                            (!string.IsNullOrEmpty(f.ProgramaNombre) && f.ProgramaNombre.ToLower().Contains(low)))
                        .ToList();
                }

                fichasFiltradas = fichasFiltradas.OrderBy(f => f.CodigoFicha).ToList();

                return Json(new { ok = true, data = fichasFiltradas }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(
                    new { ok = false, msg = "❌ Error al obtener las fichas: " + ex.Message },
                    JsonRequestBehavior.AllowGet
                );
            }
        }


        private int ObtenerTrimestreDestinoReal(int idFicha, int anio)
        {
            var ficha = db.Ficha.AsNoTracking().FirstOrDefault(f => f.IdFicha == idFicha);
            int trimestreActualFicha = ficha?.Trimestre ?? 1;

            // último trimestre académico ya creado para ese AÑO en esa ficha
            int? ultimoTrimestreCreado = db.Horario
                .Where(h => h.IdFicha == idFicha && h.Año_Horario == anio)
                .Select(h => (int?)h.Trimestre_Año)
                .DefaultIfEmpty(null)
                .Max();

            // si no hay horarios creados en ese año → se programa el trimestre actual de la ficha
            if (!ultimoTrimestreCreado.HasValue)
                return trimestreActualFicha;

            // si ya existe el del trimestre actual → siguiente
            if (ultimoTrimestreCreado.Value >= trimestreActualFicha)
                return Math.Min(7, ultimoTrimestreCreado.Value + 1);

            // caso raro: la ficha “va por más” que lo que hay creado en ese año
            return trimestreActualFicha;
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
            // EPPlus (si te lo llega a pedir en runtime)
            // OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;

            using (var tx = db.Database.BeginTransaction())
            {
                bool oldDetect = db.Configuration.AutoDetectChangesEnabled;
                bool oldValidate = db.Configuration.ValidateOnSaveEnabled;

                try
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    db.Configuration.ValidateOnSaveEnabled = false;

                    if (archivoExcel == null || archivoExcel.ContentLength == 0)
                        return Json(new { ok = false, msg = "No se cargó ningún archivo Excel." });

                    // 1) Guardar archivo temporal
                    string path = Server.MapPath("~/Uploads/");
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);

                    string filePath = Path.Combine(path, Path.GetFileName(archivoExcel.FileName));
                    archivoExcel.SaveAs(filePath);

                    // 2) Buscar ficha
                    var ficha = db.Ficha
                                  .Include("Programa_Formacion")
                                  .FirstOrDefault(f => f.IdFicha == idFicha);

                    if (ficha == null)
                        return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                    int idPrograma = ficha.IdPrograma ?? 0;
                    if (idPrograma <= 0)
                        return Json(new { ok = false, msg = "❌ La ficha no tiene programa asociado." });

                    // 3) Limpieza de planeación de esa ficha
                    var prevRt = db.ResultadoTrimestre.Where(x => x.IdFicha == idFicha).ToList();
                    if (prevRt.Any())
                        db.ResultadoTrimestre.RemoveRange(prevRt);

                    // ============================
                    // A) CACHE EXISTENTE (BD)
                    // ============================
                    // Competencias existentes del programa (para resolver IdCompetencia)
                    var compsExist = db.Competencia.AsNoTracking()
                        .Where(c => c.IdPrograma == idPrograma)
                        .Select(c => new { c.IdCompetencia, c.Nombre })
                        .ToList();

                    var compDict = compsExist
                        .GroupBy(x => Normalizar(x.Nombre))
                        .ToDictionary(g => g.Key, g => g.First().IdCompetencia);

                    // Resultados existentes de esas competencias (para resolver IdResultado)
                    var compIds = compsExist.Select(x => x.IdCompetencia).Distinct().ToList();
                    var resExist = db.ResultadoAprendizaje.AsNoTracking()
                        .Where(r => compIds.Contains(r.IdCompetencia))
                        .Select(r => new { r.IdResultado, r.IdCompetencia, r.Descripcion })
                        .ToList();

                    // key = $"{idCompetencia}|{resultadoNorm}"
                    var resDict = resExist
                        .GroupBy(x => $"{x.IdCompetencia}|{Normalizar(x.Descripcion)}")
                        .ToDictionary(g => g.Key, g => g.First().IdResultado);

                    // ============================
                    // B) LEER EXCEL A MEMORIA
                    // ============================
                    var rows = new List<TempRow>();

                    using (var package = new OfficeOpenXml.ExcelPackage(new FileInfo(filePath)))
                    {
                        var ws = package.Workbook.Worksheets["Hoja1"];
                        if (ws == null)
                            return Json(new { ok = false, msg = "❌ No se encontró la hoja 'Hoja1'." });

                        int rowCount = ws.Dimension.Rows;
                        string competenciaActual = null;

                        for (int row = 2; row <= rowCount; row++)
                        {
                            string competenciaTxt = ws.Cells[row, 4].Text?.Trim();
                            string resultadoTxt = ws.Cells[row, 6].Text?.Trim();

                            if (!string.IsNullOrEmpty(competenciaTxt))
                                competenciaActual = competenciaTxt;

                            if (string.IsNullOrWhiteSpace(competenciaActual) || string.IsNullOrWhiteSpace(resultadoTxt))
                                continue;

                            var horasPorTrim = new int[7];
                            for (int trimAcad = 1; trimAcad <= 7; trimAcad++)
                            {
                                int col = 7 + trimAcad; // 8..14
                                horasPorTrim[trimAcad - 1] = ParseNullableInt(ws.Cells[row, col].Text) ?? 0;
                            }

                            rows.Add(new TempRow
                            {
                                Competencia = competenciaActual.Trim(),
                                CompetenciaNorm = Normalizar(competenciaActual),
                                Resultado = resultadoTxt.Trim(),
                                ResultadoNorm = Normalizar(resultadoTxt),
                                Horas = horasPorTrim
                            });
                        }
                    }

                    if (!rows.Any())
                        return Json(new { ok = false, msg = "⚠️ El archivo no contiene filas válidas (competencia/resultado)." });

                    // ============================
                    // C) CREAR COMPETENCIAS FALTANTES (1 SaveChanges)
                    // ============================
                    var nuevasComps = rows
                        .GroupBy(r => r.CompetenciaNorm)
                        .Select(g => g.First())
                        .Where(x => !compDict.ContainsKey(x.CompetenciaNorm))
                        .Select(x => new Competencia
                        {
                            IdPrograma = idPrograma,
                            Nombre = x.Competencia
                        })
                        .ToList();

                    if (nuevasComps.Any())
                    {
                        db.Competencia.AddRange(nuevasComps);
                        db.ChangeTracker.DetectChanges();
                        db.SaveChanges();

                        // actualizar diccionario con IDs recién generados
                        foreach (var c in nuevasComps)
                            compDict[Normalizar(c.Nombre)] = c.IdCompetencia;
                    }

                    // ============================
                    // D) CREAR RESULTADOS FALTANTES (1 SaveChanges)
                    // ============================
                    var nuevosRes = new List<ResultadoAprendizaje>();

                    foreach (var r in rows)
                    {
                        if (!compDict.TryGetValue(r.CompetenciaNorm, out int idComp))
                            continue;

                        string key = $"{idComp}|{r.ResultadoNorm}";
                        if (resDict.ContainsKey(key)) continue;

                        nuevosRes.Add(new ResultadoAprendizaje
                        {
                            IdCompetencia = idComp,
                            Descripcion = r.Resultado,
                            DuracionResultado = 0
                        });

                        // prevenir duplicados en memoria mientras se crean
                        resDict[key] = -1;
                    }

                    if (nuevosRes.Any())
                    {
                        db.ResultadoAprendizaje.AddRange(nuevosRes);
                        db.ChangeTracker.DetectChanges();
                        db.SaveChanges();

                        // refrescar claves con IDs reales
                        foreach (var rr in nuevosRes)
                        {
                            string key = $"{rr.IdCompetencia}|{Normalizar(rr.Descripcion)}";
                            resDict[key] = rr.IdResultado;
                        }
                    }

                    // ============================
                    // E) INSERTAR RESULTADOTRIMESTRE (1 SaveChanges)
                    // ============================
                    var inserted = new HashSet<string>(); // "{idFicha}|{idResultado}|{trimAcad}"

                    foreach (var r in rows)
                    {
                        if (!compDict.TryGetValue(r.CompetenciaNorm, out int idComp))
                            continue;

                        string keyRes = $"{idComp}|{r.ResultadoNorm}";
                        if (!resDict.TryGetValue(keyRes, out int idRes) || idRes <= 0)
                            continue;

                        for (int trimAcad = 1; trimAcad <= 7; trimAcad++)
                        {
                            int horas = r.Horas[trimAcad - 1];
                            if (horas <= 0) continue;

                            string key = $"{idFicha}|{idRes}|{trimAcad}";
                            if (inserted.Contains(key)) continue;

                            inserted.Add(key);

                            db.ResultadoTrimestre.Add(new ResultadoTrimestre
                            {
                                IdFicha = idFicha,
                                IdResultado = idRes,
                                TrimestreAcad = trimAcad,
                                HorasPlaneadas = horas,
                                Horas = 0
                            });
                        }
                    }

                    db.ChangeTracker.DetectChanges();
                    db.SaveChanges();

                    tx.Commit();

                    // 6) Calcular trimestre DESTINO (académico 1–7)
                    int trimestreActualFicha = ficha.Trimestre ?? 1;
                    // 6) Calcular trimestre DESTINO real (académico 1–7) según horarios ya creados en ese año
                    int trimestreDestino = ObtenerTrimestreDestinoReal(idFicha, anio);

                    // 7) Filtrar resultados del TRIMESTRE DESTINO
                    var competenciasFiltradas = FiltrarCompetenciasPorTrimestre(idFicha, trimestreDestino);


                    return Json(new
                    {
                        ok = true,
                        msg = "✅ Planeación cargada correctamente.",
                        trimestreDestino,
                        competencias = competenciasFiltradas
                    });
                }
                catch (Exception ex)
                {
                    tx.Rollback();

                    string deepMsg = ex.InnerException?.InnerException?.Message
                                     ?? ex.InnerException?.Message
                                     ?? ex.Message;

                    return Json(new { ok = false, msg = "❌ Error al procesar el archivo: " + deepMsg });
                }
                finally
                {
                    db.Configuration.AutoDetectChangesEnabled = oldDetect;
                    db.Configuration.ValidateOnSaveEnabled = oldValidate;
                }
            }
        }

        // Clase auxiliar privada (puede ir dentro del controller)
        private class TempRow
        {
            public string Competencia { get; set; }
            public string CompetenciaNorm { get; set; }
            public string Resultado { get; set; }
            public string ResultadoNorm { get; set; }
            public int[] Horas { get; set; } // 7 posiciones (I..VII)
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

            var data = FiltrarCompetenciasPorTrimestre(idFicha, trimestre);

            if (!data.Any())
                return Json(new
                {
                    ok = false,
                    msg = $"No hay resultados de aprendizaje para el trimestre {trimestre}."
                }, JsonRequestBehavior.AllowGet);

            return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
        }


        private List<CompetenciaDTO> FiltrarCompetenciasPorTrimestre(int idFicha, int trimestreAcad)
        {
            const int SEMANAS = 12;

            // 🔥 (opcional) subir timeout SOLO para esta operación
            db.Database.CommandTimeout = 180;

            // 1) Horas requeridas por resultado SOLO del trimestre destino
            var requeridas = db.ResultadoTrimestre.AsNoTracking()
                .Where(rt => rt.IdFicha == idFicha
                          && rt.TrimestreAcad == trimestreAcad
                          && rt.HorasPlaneadas > 0)
                .GroupBy(rt => rt.IdResultado)
                .Select(g => new
                {
                    IdResultado = g.Key,
                    HorasReq = g.Sum(x => x.HorasPlaneadas)
                })
                .ToList();

            if (!requeridas.Any()) return new List<CompetenciaDTO>();

            var reqDict = requeridas.ToDictionary(x => x.IdResultado, x => x.HorasReq);
            var idsResultados = reqDict.Keys.ToList();

            // 2) Traer IDs de horarios de esa ficha HASTA el trimestre destino
            var idsHorarios = db.Horario.AsNoTracking()
                .Where(h => h.IdFicha == idFicha && h.Trimestre_Año <= trimestreAcad)
                .Select(h => h.Id_Horario)
                .ToList();

            // Si no hay horarios aún, no hay horas programadas
            Dictionary<int, int> progDict = new Dictionary<int, int>();

            if (idsHorarios.Any())
            {
                // 3) Horas programadas acumuladas (solo filas de HorarioInstructor relevantes)
                var horasProg = db.HorarioInstructor.AsNoTracking()
                    .Where(hi => hi.IdFicha == idFicha
                              && hi.IdResultado.HasValue
                              && idsResultados.Contains(hi.IdResultado.Value)
                              && idsHorarios.Contains(hi.Id_Horario))
                    .Select(hi => new
                    {
                        IdResultado = hi.IdResultado.Value,
                        hi.HoraDesde,
                        hi.HoraHasta
                    })
                    .ToList() // ✅ a memoria para calcular minutos sin que EF traduzca cosas raras
                    .Select(x => new
                    {
                        x.IdResultado,
                        Minutos = Math.Max(0, (x.HoraHasta - x.HoraDesde).TotalMinutes)
                    })
                    .GroupBy(x => x.IdResultado)
                    .Select(g => new
                    {
                        IdResultado = g.Key,
                        HorasProg = (int)Math.Round(((decimal)g.Sum(x => x.Minutos) / 60m) * SEMANAS)
                    })
                    .ToList();

                progDict = horasProg.ToDictionary(x => x.IdResultado, x => x.HorasProg);
            }

            // 4) Quedarnos SOLO con resultados que tengan pendientes (>0)
            var idsPendientes = idsResultados
                .Where(id =>
                {
                    int req = reqDict.TryGetValue(id, out var r) ? r : 0;
                    int prog = progDict.TryGetValue(id, out var p) ? p : 0;
                    return (req - prog) > 0;
                })
                .ToList();

            if (!idsPendientes.Any()) return new List<CompetenciaDTO>();

            // 5) Traer competencia y texto de resultado SOLO para esos pendientes
            var data = (
                from r in db.ResultadoAprendizaje.AsNoTracking()
                join c in db.Competencia.AsNoTracking() on r.IdCompetencia equals c.IdCompetencia
                where idsPendientes.Contains(r.IdResultado)
                select new
                {
                    Competencia = c.Nombre,
                    Resultado = r.Descripcion
                }
            )
            .ToList()
            .GroupBy(x => x.Competencia)
            .Select(g => new CompetenciaDTO
            {
                Competencia = g.Key,
                Resultados = g.Select(x => x.Resultado)
                              .Where(t => !string.IsNullOrWhiteSpace(t))
                              .Distinct()
                              .ToList()
            })
            .Where(c => c.Resultados.Any())
            .OrderBy(c => c.Competencia)
            .ToList();

            return data;
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
        public JsonResult GetResultadosPorCompetencia(string nombreCompetencia, int idFicha, int trimestreAcad)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nombreCompetencia) || idFicha <= 0 || trimestreAcad < 1 || trimestreAcad > 7)
                    return Json(new { ok = false, msg = "Parámetros inválidos." }, JsonRequestBehavior.AllowGet);

                string compTxt = nombreCompetencia.Trim();
                string compNorm = Normalizar(compTxt);

                var competencia = db.Competencia.AsNoTracking()
                    .ToList()
                    .FirstOrDefault(c => Normalizar(c.Nombre) == compNorm);

                if (competencia == null)
                    return Json(new { ok = true, data = new List<object>() }, JsonRequestBehavior.AllowGet);

                int idCompetencia = competencia.IdCompetencia;

                var resultadosBase = db.ResultadoAprendizaje.AsNoTracking()
                    .Where(r => r.IdCompetencia == idCompetencia)
                    .Select(r => new { r.IdResultado, r.Descripcion })
                    .ToList();

                if (!resultadosBase.Any())
                    return Json(new { ok = true, data = new List<object>() }, JsonRequestBehavior.AllowGet);

                var idsResultados = resultadosBase.Select(x => x.IdResultado).ToList();

                // Horas requeridas SOLO del trimestre actual
                var requeridasTrim = db.ResultadoTrimestre.AsNoTracking()
                    .Where(rt => rt.IdFicha == idFicha
                              && rt.TrimestreAcad == trimestreAcad
                              && idsResultados.Contains(rt.IdResultado))
                    .GroupBy(rt => rt.IdResultado)
                    .Select(g => new
                    {
                        IdResultado = g.Key,
                        HorasReq = g.Sum(x => x.HorasPlaneadas)
                    })
                    .ToList();

                var reqDict = requeridasTrim.ToDictionary(x => x.IdResultado, x => x.HorasReq);

                // Horas programadas acumuladas hasta el trimestre (por resultado)
                const int SEMANAS = 12;

                var horasDictadas = (
                    from hi in db.HorarioInstructor
                    join h in db.Horario on hi.Id_Horario equals h.Id_Horario
                    where hi.IdFicha == idFicha
                          && h.Trimestre_Año <= trimestreAcad
                          && hi.IdResultado.HasValue
                          && idsResultados.Contains(hi.IdResultado.Value)
                    select new
                    {
                        IdResultado = hi.IdResultado.Value,
                        hi.HoraDesde,
                        hi.HoraHasta
                    }
                )
                .ToList() // ✅ aquí ya estamos en memoria
                .Select(x => new
                {
                    x.IdResultado,
                    Minutos = Math.Max(0, (x.HoraHasta - x.HoraDesde).TotalMinutes)
                })
                .GroupBy(x => x.IdResultado)
                .Select(g => new
                {
                    IdResultado = g.Key,
                    HorasProg = (int)Math.Round(((decimal)g.Sum(x => x.Minutos) / 60m) * SEMANAS)
                })
                .ToList();

                var progDict = horasDictadas.ToDictionary(x => x.IdResultado, x => x.HorasProg);


                var salida = resultadosBase.Select(rb =>
                {
                    int req = reqDict.TryGetValue(rb.IdResultado, out var r) ? r : 0;
                    int prog = progDict.TryGetValue(rb.IdResultado, out var p) ? p : 0;

                    return new
                    {
                        IdResultado = rb.IdResultado,
                        Resultado = rb.Descripcion,
                        HorasRequeridas = req,
                        HorasProgramadas = prog,
                        HorasPendientes = Math.Max(req - prog, 0),
                        HorasExtra = Math.Max(prog - req, 0),
                        Porcentaje = req > 0 ? Math.Round((decimal)prog * 100m / req, 2) : 0
                    };
                }).ToList();

                // ✅ SOLO pendientes (horas para programar)
                salida = salida
                    .Where(x => x.HorasPendientes > 0)
                    .ToList();


                int totalReq = salida.Sum(x => x.HorasRequeridas);
                int totalProg = salida.Sum(x => x.HorasProgramadas);

                var resumenCompetencia = new
                {
                    Competencia = compTxt,
                    TotalRequeridas = totalReq,
                    TotalProgramadas = totalProg,
                    TotalPendientes = Math.Max(totalReq - totalProg, 0),
                    TotalExtra = Math.Max(totalProg - totalReq, 0),
                    Porcentaje = totalReq > 0 ? Math.Round((decimal)totalProg * 100m / totalReq, 2) : 0
                };

                return Json(new { ok = true, data = salida, resumen = resumenCompetencia }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                string real =
                    ex.InnerException?.InnerException?.Message ??
                    ex.InnerException?.Message ??
                    ex.Message;

                return Json(new { ok = false, msg = "Error: " + real }, JsonRequestBehavior.AllowGet);
            }
        }



        [HttpGet]
        public JsonResult GetInstructores(string q = null, int? top = null)
        {
            try
            {
                var query = db.Instructor.AsNoTracking()
                             .Where(i => i.EstadoInstructor == true);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    string term = q.Trim().ToLowerInvariant();
                    query = query.Where(i => (i.NombreCompletoInstructor ?? "").ToLower().Contains(term));
                }

                query = query.OrderBy(i => i.NombreCompletoInstructor);

                if (top.HasValue && top.Value > 0)
                    query = query.Take(top.Value);

                var data = query.Select(i => new
                {
                    id = i.IdInstructor,
                    nombre = i.NombreCompletoInstructor ?? "(Sin nombre)"
                }).ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
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
            using (var tx = db.Database.BeginTransaction())
            {
                try
                {
                    var asignaciones =
                        JsonConvert.DeserializeObject<List<AsignacionViewModel>>(AsignacionesJson);

                    if (asignaciones == null || !asignaciones.Any())
                        return Json(new { ok = false, msg = "⚠️ No hay asignaciones para guardar." });

                    numeroFicha = (numeroFicha ?? "").Trim();

                    var ficha = db.Ficha
                        .FirstOrDefault(f => f.CodigoFicha.ToString() == numeroFicha);

                    if (ficha == null)
                        return Json(new { ok = false, msg = "❌ Ficha no encontrada." });

                    int trimestreActualFicha = ficha.Trimestre.GetValueOrDefault();

                    if (!int.TryParse(trimestreFicha, out int trimestreSolicitado))
                        return Json(new { ok = false, msg = "❌ Trimestre académico inválido." });

                    // ✅ trimestreAnio debe ser AÑO REAL
                    if (!int.TryParse(trimestreAnio, out int anioReal))
                        return Json(new { ok = false, msg = "❌ Año inválido." });

                    if (anioReal < 100) anioReal = DateTime.Now.Year; // defensa legacy

                    if (trimestreActualFicha >= 7)
                        return Json(new { ok = false, msg = "❌ La ficha ya está en trimestre 7." });

                    // Permite mismo trimestre o siguiente (como lo tenías)
                    if (trimestreSolicitado < trimestreActualFicha ||
                        trimestreSolicitado > (trimestreActualFicha + 1))
                        return Json(new { ok = false, msg = "❌ Trimestre inválido." });

                    // ✅ duplicado POR AÑO Y TRIMESTRE
                    if (db.Horario.Any(h => h.IdFicha == ficha.IdFicha
                                         && h.Trimestre_Año == trimestreSolicitado
                                         && h.Año_Horario == anioReal))
                        return Json(new { ok = false, msg = "❌ Ya existe un horario para este trimestre y año." });

                    var horarioNuevo = new Horario
                    {
                        Año_Horario = anioReal,
                        Trimestre_Año = trimestreSolicitado, // académico 1–7
                        Fecha_Creacion = DateTime.Now,
                        IdFicha = ficha.IdFicha,
                        IdInstructorLider = idInstructorLider
                    };

                    db.Horario.Add(horarioNuevo);
                    db.SaveChanges();

                    const int SEMANAS = 12;
                    var pendientes = new List<object>();

                    var resultadosTrimestreFicha = db.ResultadoTrimestre
                        .Where(rt => rt.IdFicha == ficha.IdFicha && rt.TrimestreAcad == trimestreSolicitado)
                        .ToList();

                    var competenciasBD = db.Competencia.AsNoTracking().ToList();
                    var resultadosBD = db.ResultadoAprendizaje.AsNoTracking().ToList();

                    // ✅ ACUMULADORES para calcular pendientes reales al final
                    var acumuladoProgPorResultado = new Dictionary<int, int>();
                    var acumuladoReqPorResultado = new Dictionary<int, int>();
                    var textoPorResultado = new Dictionary<int, (string comp, string res)>();

                    var acumuladoProgPorCompetencia = new Dictionary<int, int>();
                    var acumuladoReqPorCompetencia = new Dictionary<int, int>();
                    var textoPorCompetencia = new Dictionary<int, string>();


                    foreach (var a in asignaciones)
                    {
                        // ignorar asignaciones sin instructor
                        if (!a.instructorId.HasValue || a.instructorId.Value <= 0)
                            continue;

                        string compTxt = LimpiarTexto(a.competencia);
                        string resTxt = LimpiarTexto(a.resultado);

                        if (string.IsNullOrWhiteSpace(a.horaDesde) || string.IsNullOrWhiteSpace(a.horaHasta))
                            return Json(new { ok = false, msg = "❌ Horas inválidas en asignación." });

                        TimeSpan desde = TimeSpan.Parse(a.horaDesde);
                        TimeSpan hasta = TimeSpan.Parse(a.horaHasta);

                        if (desde >= hasta)
                            return Json(new { ok = false, msg = "❌ Hora inicial no válida." });

                        double minutosSemana = (hasta - desde).TotalMinutes;
                        int horasProgramadas = (int)Math.Round((minutosSemana / 60.0) * SEMANAS);

                        int? idResultado = ResolverIdResultadoSeguro(compTxt, resTxt, competenciasBD, resultadosBD);

                        int horasRequeridas = 0;
                        int? idCompetencia = null;

                        if (idResultado.HasValue)
                        {
                            horasRequeridas = resultadosTrimestreFicha
                                .Where(rt => rt.IdResultado == idResultado.Value)
                                .Sum(rt => rt.HorasPlaneadas);

                            idCompetencia = resultadosBD
                                .Where(r => r.IdResultado == idResultado.Value)
                                .Select(r => (int?)r.IdCompetencia)
                                .FirstOrDefault();
                        }
                        else if (!string.IsNullOrWhiteSpace(compTxt))
                        {
                            var compBD = competenciasBD.FirstOrDefault(c =>
                                Normalizar(c.Nombre) == Normalizar(compTxt));

                            if (compBD != null)
                            {
                                idCompetencia = compBD.IdCompetencia;

                                var idsResComp = resultadosBD
                                    .Where(r => r.IdCompetencia == compBD.IdCompetencia)
                                    .Select(r => r.IdResultado)
                                    .ToList();

                                horasRequeridas = resultadosTrimestreFicha
                                    .Where(rt => idsResComp.Contains(rt.IdResultado))
                                    .Sum(rt => rt.HorasPlaneadas);
                            }
                        }

                        // ✅ ACUMULAR horas por RESULTADO o por COMPETENCIA
                        if (idResultado.HasValue)
                        {
                            int idRes = idResultado.Value;

                            if (!acumuladoProgPorResultado.ContainsKey(idRes))
                                acumuladoProgPorResultado[idRes] = 0;
                            acumuladoProgPorResultado[idRes] += horasProgramadas;

                            if (!acumuladoReqPorResultado.ContainsKey(idRes))
                                acumuladoReqPorResultado[idRes] = horasRequeridas;

                            textoPorResultado[idRes] = (compTxt, resTxt);
                        }
                        else if (idCompetencia.HasValue)
                        {
                            int idComp = idCompetencia.Value;

                            if (!acumuladoProgPorCompetencia.ContainsKey(idComp))
                                acumuladoProgPorCompetencia[idComp] = 0;
                            acumuladoProgPorCompetencia[idComp] += horasProgramadas;

                            if (!acumuladoReqPorCompetencia.ContainsKey(idComp))
                                acumuladoReqPorCompetencia[idComp] = horasRequeridas;

                            textoPorCompetencia[idComp] = compTxt;
                        }


                        db.Asignacion_horario.Add(new Asignacion_horario
                        {
                            Dia = a.dia,
                            HoraDesde = desde,
                            HoraHasta = hasta,
                            IdInstructor = a.instructorId.Value,  // 👈 .Value porque ya filtramos los null
                            IdFicha = ficha.IdFicha,
                            HorasProgramadas = horasProgramadas,
                            HorasTotales = horasRequeridas
                        });

                        db.HorarioInstructor.Add(new HorarioInstructor
                        {
                            IdInstructor = a.instructorId.Value,
                            IdFicha = ficha.IdFicha,
                            Id_Horario = horarioNuevo.Id_Horario,
                            Dia = a.dia,
                            HoraDesde = desde,
                            HoraHasta = hasta,
                            Competencia = string.IsNullOrWhiteSpace(compTxt) ? null : compTxt,
                            Resultado = string.IsNullOrWhiteSpace(resTxt) ? null : resTxt,
                            IdResultado = idResultado,
                            IdCompetencia = idCompetencia
                        });
                    }

                    db.SaveChanges();

                    // ✅ RECONSTRUIR pendientes reales (acumulados)
                    pendientes = new List<object>();

                    // Pendientes por RESULTADO
                    foreach (var kv in acumuladoReqPorResultado)
                    {
                        int idRes = kv.Key;
                        int req = kv.Value;
                        int prog = acumuladoProgPorResultado.TryGetValue(idRes, out var p) ? p : 0;

                        if (req > prog)
                        {
                            var txt = textoPorResultado[idRes];
                            pendientes.Add(new
                            {
                                Tipo = "RESULTADO",
                                Competencia = txt.comp,
                                Resultado = txt.res,
                                HorasFaltantes = req - prog,
                                TrimestreAcad = trimestreSolicitado, // ✅ trimestre al que pertenecen
                                AnioHorario = anioReal
                            });
                        }
                    }

                    // Pendientes por COMPETENCIA completa
                    foreach (var kv in acumuladoReqPorCompetencia)
                    {
                        int idComp = kv.Key;
                        int req = kv.Value;
                        int prog = acumuladoProgPorCompetencia.TryGetValue(idComp, out var p) ? p : 0;

                        if (req > prog)
                        {
                            pendientes.Add(new
                            {
                                Tipo = "COMPETENCIA",
                                Competencia = textoPorCompetencia[idComp],
                                Resultado = (string)null,
                                HorasFaltantes = req - prog,
                                TrimestreAcad = trimestreSolicitado, // ✅ trimestre
                                AnioHorario = anioReal
                            });
                        }
                    }

                    horarioNuevo.CompetenciasPendientes =
                        pendientes.Any() ? JsonConvert.SerializeObject(pendientes) : null;

                    db.Entry(horarioNuevo).State = EntityState.Modified;
                    db.SaveChanges();

                    ficha.Trimestre = Math.Min(7, trimestreSolicitado);
                    db.Entry(ficha).State = EntityState.Modified;
                    db.SaveChanges();

                    tx.Commit();
                    return Json(new { ok = true, msg = "✅ Horario creado correctamente." });
                }
                catch (DbEntityValidationException ex)
                {
                    tx.Rollback();

                    var errores = ex.EntityValidationErrors
                        .SelectMany(evr => evr.ValidationErrors.Select(ve => new
                        {
                            Entidad = evr.Entry.Entity.GetType().Name,
                            Propiedad = ve.PropertyName,
                            Error = ve.ErrorMessage
                        }))
                        .ToList();

                    var detalle = string.Join(" | ", errores.Select(e =>
                        $"Entidad: {e.Entidad}, Propiedad: {e.Propiedad}, Error: {e.Error}"));

                    return Json(new
                    {
                        ok = false,
                        msg = "❌ Error de validación: " + detalle
                    });
                }

                catch (Exception ex)
                {
                    tx.Rollback();

                    string real = ex.InnerException?.InnerException?.Message
                                  ?? ex.InnerException?.Message
                                  ?? ex.Message;

                    return Json(new { ok = false, msg = "❌ Error: " + real });
                }
            }
        }


        private string LimpiarTexto(string t)
        {
            if (string.IsNullOrWhiteSpace(t) || t == "undefined") return "";
            return t.Trim();
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
                        TrimestreFicha = f.Trimestre,        // 1–7
                        AnioHorario = h.Año_Horario,         // ✅ año real
                        TrimestreAcademico = h.Trimestre_Año,// 1–7
                        FechaCreacion = h.Fecha_Creacion,
                        InstructorLider = inst.NombreCompletoInstructor
                    };

                var data = query.AsEnumerable().Select(x => new
                {
                    x.Id_Horario,
                    x.IdFicha,
                    CodigoFicha = x.CodigoFicha?.ToString(),
                    x.ProgramaNombre,
                    FechaInicioFicha = x.FechaInicio?.ToString("yyyy-MM-dd"),
                    FechaFinFicha = x.FechaFin?.ToString("yyyy-MM-dd"),
                    TrimestreFicha = x.TrimestreFicha,
                    Año_Horario = x.AnioHorario,
                    TrimestreAcademico = x.TrimestreAcademico,
                    FechaCreacionHorario = x.FechaCreacion?.ToString("yyyy-MM-dd HH:mm"),
                    InstructorLider = x.InstructorLider ?? "Sin asignar"
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

        public ActionResult VerHorarioFicha(int idHorario)
        {
            const int SEMANAS = 12;

            var horario = db.Horario
                .Include(h => h.Ficha)
                .FirstOrDefault(h => h.Id_Horario == idHorario);

            if (horario == null) return HttpNotFound();

            int idFicha = horario.IdFicha;
            int trimestreAcad = horario.Trimestre_Año; // 1–7

            // 1) HORAS REQUERIDAS POR RESULTADO (desde ResultadoTrimestre)
            var requeridas = (
                from rt in db.ResultadoTrimestre
                join r in db.ResultadoAprendizaje on rt.IdResultado equals r.IdResultado
                join c in db.Competencia on r.IdCompetencia equals c.IdCompetencia
                where rt.IdFicha == idFicha
                      && rt.TrimestreAcad == trimestreAcad
                      && rt.HorasPlaneadas > 0
                select new
                {
                    Competencia = c.Nombre,
                    Resultado = r.Descripcion,
                    HorasReq = rt.HorasPlaneadas
                }
            ).ToList();

            // 2) DETALLE COMPLETO DEL HORARIO (franjas)
            var detalleHorario = db.HorarioInstructor
                .Where(hi => hi.Id_Horario == idHorario)
                .Include("Instructor")
                .ToList();

            // ============================
            // A) HORAS PROGRAMADAS POR (COMPETENCIA, RESULTADO)
            // ============================
            var progPorResultado = detalleHorario
                .Where(h => !string.IsNullOrWhiteSpace(h.Competencia))
                .GroupBy(h => new { h.Competencia, h.Resultado })
                .ToDictionary(
                    g => g.Key,
                    g => (int)Math.Round(
                            g.Sum(a => (a.HoraHasta - a.HoraDesde).TotalHours * SEMANAS)
                         )
                );

            // 3) TRAZABILIDAD POR RESULTADO
            var trazabilidad = requeridas
                .GroupBy(r => new { r.Competencia, r.Resultado })
                .Select(g =>
                {
                    int req = g.Sum(x => x.HorasReq);

                    progPorResultado.TryGetValue(
                        new { g.Key.Competencia, g.Key.Resultado },
                        out int prog
                    );

                    return new TrazabilidadResultadoVM
                    {
                        Competencia = g.Key.Competencia,
                        Resultado = g.Key.Resultado,
                        HorasRequeridas = req,
                        HorasProgramadas = prog,
                        // HorasPendientes y HorasExtra se calculan en el VM
                        Porcentaje = req > 0
                            ? Math.Round((decimal)prog * 100m / req, 2)
                            : 0
                    };
                })
                .OrderBy(x => x.Competencia)
                .ThenBy(x => x.Resultado)
                .ToList();

            // ============================
            // B) HORAS PROGRAMADAS POR COMPETENCIA (sumando todos los resultados)
            // ============================

            // requeridas por competencia
            var reqPorComp = requeridas
                .GroupBy(r => r.Competencia)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.HorasReq)
                );

            // programadas por competencia (todas las franjas de esa competencia)
            var progPorComp = detalleHorario
                .Where(h => !string.IsNullOrWhiteSpace(h.Competencia))
                .GroupBy(h => h.Competencia)
                .ToDictionary(
                    g => g.Key,
                    g => (int)Math.Round(
                            g.Sum(a => (a.HoraHasta - a.HoraDesde).TotalHours * SEMANAS)
                         )
                );

            var competenciasResumen = reqPorComp
                .Select(kv =>
                {
                    string comp = kv.Key;
                    int req = kv.Value;
                    progPorComp.TryGetValue(comp, out int prog);

                    return new CompetenciaResumenVM
                    {
                        Competencia = comp,
                        HorasRequeridas = req,
                        HorasProgramadas = prog,
                        // HorasPendientes y HorasExtra se calculan en el VM
                        Porcentaje = req > 0
                            ? Math.Round((decimal)prog * 100m / req, 2)
                            : 0
                    };
                })
                .OrderBy(c => c.Competencia)
                .ToList();

            // ============================
            // C) TOTALES GENERALES (para la tarjeta de arriba)
            // ============================
            int totalReq = competenciasResumen.Sum(x => x.HorasRequeridas);
            int totalProg = competenciasResumen.Sum(x => x.HorasProgramadas);
            int totalPend = competenciasResumen.Sum(x => x.HorasPendientes);

            // ============================
            // VM FINAL
            // ============================
            var vm = new VerHorarioFichaVM
            {
                IdHorario = idHorario,
                IdFicha = idFicha,
                CodigoFicha = horario.Ficha?.CodigoFicha?.ToString() ?? "N/A",
                TrimestreActual = trimestreAcad,
                AnioHorario = horario.Año_Horario,
                DetalleHorario = detalleHorario,
                Trazabilidad = trazabilidad,
                CompetenciasResumen = competenciasResumen,
                TotalRequeridas = totalReq,
                TotalProgramadas = totalProg,
                TotalPendientes = totalPend
            };


            return View(vm);
        }

        [HttpGet]
        public JsonResult GetPendientesParaProximoTrimestre(int? idFicha, int? anio, int? trimestreDestino)
        {
            try
            {
                if (!idFicha.HasValue || !anio.HasValue || !trimestreDestino.HasValue)
                    return Json(new { ok = false, msg = "Faltan parámetros (idFicha, anio, trimestreDestino).", data = new List<object>() },
                                JsonRequestBehavior.AllowGet);

                if (idFicha.Value <= 0 || anio.Value <= 0 || trimestreDestino.Value < 1 || trimestreDestino.Value > 7)
                    return Json(new { ok = false, msg = "Parámetros inválidos.", data = new List<object>() },
                                JsonRequestBehavior.AllowGet);

                int trimestreAnterior = Math.Max(trimestreDestino.Value - 1, 1);

                var horarioAnterior = db.Horario
                    .Where(h => h.IdFicha == idFicha.Value
                             && h.Año_Horario == anio.Value
                             && h.Trimestre_Año == trimestreAnterior)
                    .OrderByDescending(h => h.Fecha_Creacion)
                    .FirstOrDefault();

                if (horarioAnterior == null || string.IsNullOrWhiteSpace(horarioAnterior.CompetenciasPendientes))
                    return Json(new { ok = true, data = new List<object>() }, JsonRequestBehavior.AllowGet);

                var arr = JArray.Parse(horarioAnterior.CompetenciasPendientes);

                var data = arr
                    .OfType<JObject>()
                    .Select(o => new
                    {
                        Tipo = ((string)o["Tipo"] ?? "").Trim().ToUpperInvariant(),
                        Competencia = ((string)o["Competencia"] ?? "").Trim(),
                        Resultado = ((string)o["Resultado"] ?? "").Trim(),
                        HorasFaltantes = (int?)(o["HorasFaltantes"] ?? o["HorasPendientes"]) ?? 0,
                        TrimestreAcad = (int?)o["TrimestreAcad"]
                    })
                    .Where(x =>
                        x.HorasFaltantes > 0 &&
                        !string.IsNullOrWhiteSpace(x.Resultado) &&
                        (x.TrimestreAcad == null || x.TrimestreAcad == trimestreAnterior) &&
                        (string.IsNullOrEmpty(x.Tipo) || x.Tipo == "RESULTADO")
                    )
                    .OrderBy(x => x.Competencia)
                    .ThenBy(x => x.Resultado)
                    .ToList();

                return Json(new { ok = true, data }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message, data = new List<object>() },
                            JsonRequestBehavior.AllowGet);
            }
        }




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

        public ActionResult CrearSiguienteHorario(int idFicha, int trimestre, int anio)
        {
            // trimestre = trimestre actual del horario que estás viendo (1–7)
            int trimestreSiguiente = (trimestre < 7) ? trimestre + 1 : 7;

            return RedirectToAction("Index", new
            {
                idFicha = idFicha,
                anio = anio,
                trimestreDestino = trimestreSiguiente
            });
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

        [HttpGet]
        public JsonResult GetPendientesHorarioAnterior(int idFicha)
        {
            try
            {
                // Buscar el último horario de la ficha
                var ultimoHorario = db.Horario
                    .Where(h => h.IdFicha == idFicha)
                    .OrderByDescending(h => h.Fecha_Creacion)
                    .FirstOrDefault();

                if (ultimoHorario == null || string.IsNullOrWhiteSpace(ultimoHorario.CompetenciasPendientes))
                    return Json(new { ok = true, pendientes = new List<object>() }, JsonRequestBehavior.AllowGet);

                var pendientes = JsonConvert.DeserializeObject<List<dynamic>>(ultimoHorario.CompetenciasPendientes)
                                 ?? new List<dynamic>();


                return Json(new { ok = true, pendientes }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = ex.Message, pendientes = new List<object>() },
                            JsonRequestBehavior.AllowGet);
            }
        }

        private Competencia BuscarOCrearCompetencia(int idPrograma, string nombre, List<Competencia> cache)
        {
            string nomNorm = Normalizar(nombre);

            var comp = cache.FirstOrDefault(c =>
                c.IdPrograma == idPrograma && Normalizar(c.Nombre) == nomNorm);

            if (comp != null) return comp;

            comp = new Competencia
            {
                IdPrograma = idPrograma,
                Nombre = nombre.Trim(),
                DuracionTotal = null
            };

            db.Competencia.Add(comp);

            cache.Add(comp);
            return comp;
        }

        private ResultadoAprendizaje BuscarOCrearResultado(int idCompetencia, string descripcion, List<ResultadoAprendizaje> cache)
        {
            string resNorm = Normalizar(descripcion);

            var res = cache.FirstOrDefault(r =>
                r.IdCompetencia == idCompetencia && Normalizar(r.Descripcion) == resNorm);

            if (res != null) return res;

            res = new ResultadoAprendizaje
            {
                IdCompetencia = idCompetencia,
                Descripcion = descripcion.Trim(),
                DuracionResultado = 0
            };

            db.ResultadoAprendizaje.Add(res);

            cache.Add(res);
            return res;
        }


        private string Normalizar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "";

            texto = texto.Trim().ToUpperInvariant();

            // 1) Quitar dobles espacios
            while (texto.Contains("  "))
                texto = texto.Replace("  ", " ");

            // 2) Quitar tildes / diacríticos
            var normalized = texto.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (char c in normalized)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }

            // 3) Retornar limpio
            return sb.ToString().Trim();
        }

        private (DateTime Inicio, DateTime Fin) GetRangoTrimestreAnio(int anio, int trimestre)
        {
            int mesInicio;
            switch (trimestre)
            {
                case 1: mesInicio = 1; break;   // Ene–Mar
                case 2: mesInicio = 4; break;   // Abr–Jun
                case 3: mesInicio = 7; break;   // Jul–Sep
                case 4: mesInicio = 10; break;  // Oct–Dic
                default: throw new ArgumentOutOfRangeException(nameof(trimestre));
            }

            var inicio = new DateTime(anio, mesInicio, 1);
            var fin = inicio.AddMonths(3).AddDays(-1);
            return (inicio, fin);
        }

        /// <summary>
        /// Calcula en qué trimestre de la ficha (1–7) se encuentra
        /// en la fecha de inicio del trimestre del año.
        /// </summary>
        private int CalcularTrimestreFicha(DateTime fechaInicioFicha, DateTime inicioTrimestreAnio)
        {
            if (inicioTrimestreAnio < fechaInicioFicha)
                return 0; // el trimestre del año comienza antes de que empiece la ficha

            var meses = ((inicioTrimestreAnio.Year - fechaInicioFicha.Year) * 12)
                        + (inicioTrimestreAnio.Month - fechaInicioFicha.Month);

            var trimestreFicha = (meses / 3) + 1;

            if (trimestreFicha < 1) trimestreFicha = 1;
            if (trimestreFicha > 7) trimestreFicha = 7;

            return trimestreFicha;
        }


        private int? ResolverIdResultadoSeguro(
    string competenciaTxt,
    string resultadoTxt,
    List<Competencia> competenciasBD,
    List<ResultadoAprendizaje> resultadosBD)
        {
            if (string.IsNullOrWhiteSpace(resultadoTxt))
                return null;

            string compNorm = Normalizar(competenciaTxt);
            string resNorm = Normalizar(resultadoTxt);

            // ==========================================
            // 1) Si tenemos competencia, filtramos candidatos por competencia
            // ==========================================
            List<ResultadoAprendizaje> candidatos;

            if (!string.IsNullOrWhiteSpace(competenciaTxt))
            {
                var comp = competenciasBD.FirstOrDefault(c => Normalizar(c.Nombre) == compNorm);

                if (comp != null)
                {
                    candidatos = resultadosBD
                        .Where(r => r.IdCompetencia == comp.IdCompetencia)
                        .ToList();
                }
                else
                {
                    // si no encuentra la competencia, no filtramos por ella (fallback)
                    candidatos = resultadosBD.ToList();
                }
            }
            else
            {
                candidatos = resultadosBD.ToList();
            }

            if (!candidatos.Any()) return null;

            // ==========================================
            // 2) Coincidencia EXACTA normalizada
            // ==========================================
            var exacto = candidatos.FirstOrDefault(r => Normalizar(r.Descripcion) == resNorm);
            if (exacto != null) return exacto.IdResultado;

            // ==========================================
            // 3) Coincidencia por contiene (segura)
            // ==========================================
            var contiene = candidatos.FirstOrDefault(r =>
            {
                string rNorm = Normalizar(r.Descripcion);
                return rNorm.Contains(resNorm) || resNorm.Contains(rNorm);
            });
            if (contiene != null) return contiene.IdResultado;

            // ==========================================
            // 4) Coincidencia por palabras (mínimo 2)
            // ==========================================
            var palabrasRes = resNorm.Split(' ')
                .Where(p => p.Length > 3)
                .ToList();

            if (palabrasRes.Count >= 2)
            {
                foreach (var r in candidatos)
                {
                    var palabrasBD = Normalizar(r.Descripcion)
                        .Split(' ')
                        .Where(p => p.Length > 3)
                        .ToList();

                    int coincidencias = palabrasRes.Count(p => palabrasBD.Contains(p));
                    if (coincidencias >= 2)
                        return r.IdResultado;
                }
            }

            // No encontró nada
            return null;
        }

    }
}