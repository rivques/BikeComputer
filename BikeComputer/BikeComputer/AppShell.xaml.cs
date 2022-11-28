using BikeComputer.Views;
using System;
using System.Collections.Generic;
using Xamarin.Forms;

namespace BikeComputer
{
    public partial class AppShell : Xamarin.Forms.Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute(nameof(MainPage), typeof(MainPage));
        }

    }
}
