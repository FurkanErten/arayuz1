using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace arayuz_deneme_1
{
    class data_cagir
    {
        public static void Data_ekle(Grid grd, UserControl data)
        {
            if (grd.Children.Count > 0)
            {
                grd.Children.Clear();
                grd.Children.Add(data);
            }
            else
            {
                grd.Children.Add(data);
            }
        }
    }
}
