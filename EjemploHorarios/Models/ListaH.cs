using System;
using System.Collections.Generic;

namespace EjemploHorarios.Models.ViewModels
{
    public class ListaHorarioVM
    {
        public int IdHorario { get; set; }
        public string NombreHorario { get; set; }
        public string NumeroFicha { get; set; }
        public string Trimestre { get; set; }
        public List<AsignacionListaVM> Asignaciones { get; set; }
    }

    public class AsignacionListaVM
    {
        public string Dia { get; set; }
        public string HoraDesde { get; set; }
        public string HoraHasta { get; set; }
        public string InstructorNombre { get; set; }
        public string ResultadoDesc { get; set; }
    }
}
