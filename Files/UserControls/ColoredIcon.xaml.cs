using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Files.UserControls
{
    public sealed partial class ColoredIcon : UserControl
    {
        public string BaseLayerGlyph
        {
            get => (string)GetValue(BaseLayerPathProperty);
            set => SetValue(BaseLayerPathProperty, value);
        }

        public string OverlayLayerGlyph
        {
            get => (string)GetValue(OverlayLayerPathProperty);
            set => SetValue(OverlayLayerPathProperty, value);
        }

        // Using a DependencyProperty as the backing store for OverlayLayerPath.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty OverlayLayerPathProperty =
            DependencyProperty.Register(nameof(OverlayLayerGlyph), typeof(string), typeof(ColoredIcon), new PropertyMetadata(null));


        // Using a DependencyProperty as the backing store for BaseLayerPath.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty BaseLayerPathProperty =
            DependencyProperty.Register(nameof(BaseLayerGlyph), typeof(string), typeof(ColoredIcon), new PropertyMetadata(null));


        public ColoredIcon()
        {
            this.InitializeComponent();
        }
    }
}
