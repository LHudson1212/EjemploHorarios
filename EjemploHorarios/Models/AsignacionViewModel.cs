using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace EjemploHorarios.Models
{
    public class AsignacionViewModel
    {
        public string dia { get; set; }
        public string horaDesde { get; set; }
        public string horaHasta { get; set; }
        public int? instructorId { get; set; }
        public string competencia { get; set; }
        public string resultado { get; set; }
    }
}