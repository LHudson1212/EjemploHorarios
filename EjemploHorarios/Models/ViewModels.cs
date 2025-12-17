using System;
using System.Collections.Generic;
using EjemploHorarios.Models;


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

    public class TrazabilidadResultadoVM
    {
        public string Competencia { get; set; }
        public string Resultado { get; set; }
        public int HorasRequeridas { get; set; }
        public int HorasProgramadas { get; set; }
        public int HorasPendientes => Math.Max(HorasRequeridas - HorasProgramadas, 0);
        public int HorasExtra => Math.Max(HorasProgramadas - HorasRequeridas, 0);
        public decimal Porcentaje { get; set; }
    }

    public class CompetenciaResumenVM
    {
        public string Competencia { get; set; }
        public int HorasRequeridas { get; set; }
        public int HorasProgramadas { get; set; }
        public int HorasPendientes => Math.Max(HorasRequeridas - HorasProgramadas, 0);
        public int HorasExtra => Math.Max(HorasProgramadas - HorasRequeridas, 0);

        public decimal Porcentaje { get; set; }  // puede ser 110, 140...
    }


    public class VerHorarioFichaVM
    {
        public int IdHorario { get; set; }
        public int IdFicha { get; set; }
        public string CodigoFicha { get; set; }
        public int TrimestreActual { get; set; }
        public int? AnioHorario { get; set; }
        public List<HorarioInstructor> DetalleHorario { get; set; }
        public List<TrazabilidadResultadoVM> Trazabilidad { get; set; }
        // ✅ NUEVO: % por competencia completa
        public List<CompetenciaResumenVM> CompetenciasResumen { get; set; }
        public int TotalRequeridas { get; set; }
        public int TotalProgramadas { get; set; }
        public int TotalPendientes { get; set; }

    }

}
