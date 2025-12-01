using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace EjemploHorarios.Models
{
    public class Competencias_Pendientes
    {
        public class CompetenciaDTO_Extendida
        {
            public string Competencia { get; set; }
            public string Resultado { get; set; }

            public int HorasRequeridas { get; set; }
            public int HorasPendientes { get; set; }

            public bool EsPendiente { get; set; }
        }

        public class CompetenciaPendienteDTO
        {
            public string Competencia { get; set; }
            public string Resultado { get; set; }
            public int HorasDadas { get; set; }
            public int HorasRequeridas { get; set; }
            public int HorasFaltantes { get; set; }
        }

    }
}