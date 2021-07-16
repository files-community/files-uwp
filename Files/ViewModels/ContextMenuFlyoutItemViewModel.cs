using Files.UserControls;
using System.Collections.Generic;
using System.Windows.Input;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace Files.ViewModels
{
    public class ContextMenuFlyoutItemViewModel
    {
        public bool ShowItem { get; set; } = true;
        public ICommand Command { get; set; }
        public object CommandParameter { get; set; }
        public string Glyph { get; set; }
        public string GlyphFontFamilyName { get; set; }
        public string Text { get; set; }
        public object Tag { get; set; }
        public ItemType ItemType { get; set; }
        public bool IsSubItem { get; set; }
        public List<ContextMenuFlyoutItemViewModel> Items { get; set; } = new List<ContextMenuFlyoutItemViewModel>();
        public BitmapImage BitmapIcon { get; set; }

        /// <summary>
        /// Only show the item when the shift key is held
        /// </summary>
        public bool ShowOnShift { get; set; }

        /// <summary>
        /// Only show when one item is selected
        /// </summary>
        public bool SingleItemOnly { get; set; }

        /// <summary>
        /// True if the item is shown in the recycle bin
        /// </summary>
        public bool ShowInRecycleBin { get; set; }

        /// <summary>
        /// True if the item is shown in cloud drive folders
        /// </summary>
        public bool ShowInCloudDrive { get; set; }

        public KeyboardAccelerator KeyboardAccelerator { get; set; }
        public bool IsChecked { get; set; }
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// A unique identifier that can be used to save preferences for menu items
        /// </summary>
        public string ID { get; set; }

        public bool IsPrimary { get; set; }

        public bool CollapseLabel { get; set; }

        public ColoredIconModel ColoredIcon { get; set; }

        public bool ShowLoadingIndicator { get; set; }

        public bool IsHidden { get; set; }
    }

    public enum ItemType
    {
        Item,
        Separator,
        Toggle,
    }

    public struct ColoredIconModel
    {
        public string OverlayLayerGlyph { get; set; }
        public string BaseLayerGlyph { get; set; }

        public ColoredIcon ToColoredIcon() => new ColoredIcon()
        {
            OverlayLayerGlyph = OverlayLayerGlyph,
            BaseLayerGlyph = BaseLayerGlyph,
        };

        public bool IsValid => !string.IsNullOrEmpty(BaseLayerGlyph);
    }
}