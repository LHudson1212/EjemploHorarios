using System.Collections.Generic;

namespace EjemploHorarios.Models.ViewModels
{
    public class PlanificacionVM
    {
        public string NumeroFicha { get; set; }
        public List<ProgramaVM> Programas { get; set; }
        public List<InstructorVM> Instructores { get; set; }

        public List<int> ResultadosYaDictados { get; set; }
    }

    public class ProgramaVM
    {
        public int Id_Programa { get; set; }
        public string Nombre { get; set; }
        public List<CompetenciaVM> Competencias { get; set; }
    }

    public class CompetenciaVM
    {
        public int Id_Competencias { get; set; }
        public string Nombre { get; set; }
        public int RedConocimientoId { get; set; }
        public List<ResultadoVM> Resultados { get; set; }
    }

    public class ResultadoVM
    {
        public int Id_Resultado { get; set; }
        public string Descripcion { get; set; }

        // 🔥 Nueva propiedad: indica si ya fue dictado en el horario anterior
        public bool YaDictado { get; set; }
    }

    public class InstructorVM
    {
        public int Id_Instructor { get; set; }
        public string Nombre { get; set; }
        public int RedConocimientoId { get; set; }
    }
}
