using EjemploHorarios.Models;
using System;
using System.Linq;
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

                // Rango del trimestre solicitado
                var inicioTrimestre = new DateTime(anio, ((trimestre - 1) * 3) + 1, 1);
                var finTrimestre = inicioTrimestre.AddMonths(3).AddDays(-1);

                // Para cálculo de trimestre relativo a la FechaInFicha
                int startYear = inicioTrimestre.Year;
                int startMonth = inicioTrimestre.Month;

                // Consulta: Fichas EN EJECUCIÓN y activas en cualquier día del trimestre (solapamiento)
                var fichas = (from f in db.Ficha
                              join p in db.Programa_Formacion on f.IdPrograma equals p.IdPrograma into pg
                              from p in pg.DefaultIfEmpty()
                              where f.EstadoFicha == true
                                    && f.FechaInFicha <= finTrimestre
                                    && (f.FechaFinFicha == null || f.FechaFinFicha >= inicioTrimestre)
                              select new
                              {
                                  f.IdFicha,
                                  f.CodigoFicha,
                                  f.IdPrograma,
                                  ProgramaNombre = (p != null ? p.DenominacionPrograma : null),
                                  EnEjecucion = f.EstadoFicha,
                                  // Cálculo del trimestre de la ficha relativo al inicio del trimestre elegido
                                  TrimestreDeLaFicha = f.FechaInFicha == null
                                      ? 1
                                      : (
                                          (
                                              ((startYear - f.FechaInFicha.Value.Year) * 12)
                                              + (startMonth - f.FechaInFicha.Value.Month)
                                          ) < 0
                                          ? 1
                                          : (
                                              (
                                                  ((startYear - f.FechaInFicha.Value.Year) * 12)
                                                  + (startMonth - f.FechaInFicha.Value.Month)
                                              ) / 3
                                              + 1
                                            )
                                        )
                              })
                              .OrderBy(x => x.CodigoFicha)
                              .ToList();

                return Json(new { ok = true, data = fichas }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, msg = "Error al filtrar fichas: " + ex.Message }, JsonRequestBehavior.AllowGet);
            }
        }
    }
}
