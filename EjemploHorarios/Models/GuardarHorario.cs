using System.Collections.Generic;

namespace EjemploHorarios.Models.ViewModels
{
    public class AsignacionDTO
    {
        public int programaId { get; set; }
        public int resultadoId { get; set; }
        public int instructorId { get; set; }
        public string dia { get; set; }
        public string desde { get; set; }
        public string hasta { get; set; }
    }

    public class GuardarHorarioDTO
    {
        public string numeroFicha { get; set; }
        public string nombreHorario { get; set; }
        public string trimestre { get; set; }
        public List<AsignacionDTO> asignaciones { get; set; }
    }
}
