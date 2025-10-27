using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace EjemploHorarios.Models.ViewModels
{
    public class CrearSiguienteHorarioVM
    {
        [Display(Name = "Número de Ficha")]
        public string NumeroFicha { get; set; }

        [Display(Name = "Horario Anterior")]
        public string NombreAnterior { get; set; }

        [Display(Name = "Nuevo Nombre del Horario")]
        [Required(ErrorMessage = "Debes ingresar un nombre para el nuevo horario.")]
        public string NuevoNombre { get; set; }

        public int IdAnterior { get; set; }

        // 🚀 Añadimos esto para reutilizar la estructura del Index
        public List<ProgramaVM> Programas { get; set; }
        public List<InstructorVM> Instructores { get; set; }
    }


}
