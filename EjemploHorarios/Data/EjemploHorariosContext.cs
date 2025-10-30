using EjemploHorarios.Models;
using EjemploHorarios.Models.ViewModels;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Web;

namespace EjemploHorarios.Data
{
    public class EjemploHorariosContext : DbContext
    {
        // You can add custom code to this file. Changes will not be overwritten.
        // 
        // If you want Entity Framework to drop and regenerate your database
        // automatically whenever you change your model schema, please use data migrations.
        // For more information refer to the documentation:
        // http://msdn.microsoft.com/en-us/data/jj591621.aspx

        public EjemploHorariosContext() : base("name=EjemploHorariosContext")
        {
        }
      

        public virtual DbSet<Instructor> Instructor { get; set; }
        public virtual DbSet<Ficha> Ficha { get; set; }
        public virtual DbSet<Diseño_Curricular> Diseño_Curricular { get; set; }
        public virtual DbSet<Programa_Formacion> Programa_Formacion { get; set; }
        public virtual DbSet<Horario> Horario { get; set; }
        public virtual DbSet<Asignacion_horario> Asignacion_horario { get; set; }

    }

}
        
     


 
          

     






