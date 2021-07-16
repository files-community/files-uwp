using Files.UserControls;
using Files.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Files.Helpers.ContextFlyouts
{
    public static class ItemModelListToContextFlyoutHelper
    {
        public static List<MenuFlyoutItemBase> GetMenuFlyoutItemsFromModel(List<ContextMenuFlyoutItemViewModel> items)
        {
            var flyout = new List<MenuFlyoutItemBase>();
            items.ForEach(i =>
            {
                flyout.Add(GetMenuItem(i));
            });
            return flyout;
        }

        public static (List<ICommandBarElement> primaryElements, List<ICommandBarElement> secondaryElements) GetAppBarItemsFromModel(List<ContextMenuFlyoutItemViewModel> items)
        {
            var primaryModels = items.Where(i => i.IsPrimary).ToList();
            var secondaryModels = items.Except(primaryModels).ToList();

            if (secondaryModels.Last().ItemType == ItemType.Separator)
            {
                secondaryModels.RemoveAt(secondaryModels.Count - 1);
            }

            var primary = new List<ICommandBarElement>();
            primaryModels.ForEach(i => primary.Add(GetCommandBarItem(i)));
            var secondary = new List<ICommandBarElement>();
            secondaryModels.ForEach(i => secondary.Add(GetCommandBarItem(i)));

            return (primary, secondary);
        }

        /// <summary>
        /// Same as GetAppBarItemsFromModel, but ignores the IsPrimary property and returns one list
        /// </summary>
        /// <param name="items"></param>
        /// <returns></returns>
        public static List<ICommandBarElement> GetAppBarButtonsFromModelIgnorePrimary(List<ContextMenuFlyoutItemViewModel> items)
        {
            var elements = new List<ICommandBarElement>();
            items.ForEach(i => elements.Add(GetCommandBarItem(i)));
            return elements;
        }

        private static MenuFlyoutItemBase GetMenuItem(ContextMenuFlyoutItemViewModel item)
        {
            return item.ItemType switch
            {
                ItemType.Separator => new MenuFlyoutSeparator(),
                _ => GetMenuFlyoutItem(item),
            };
        }

        private static MenuFlyoutItemBase GetMenuFlyoutItem(ContextMenuFlyoutItemViewModel item, bool isToggle = false)
        {
            if (item.Items?.Count > 0)
            {
                var flyoutSubItem = new MenuFlyoutSubItem()
                {
                    Text = item.Text,
                    Tag = item.Tag,
                };
                item.Items.ForEach(i =>
                {
                    flyoutSubItem.Items.Add(GetMenuItem(i));
                });
                return flyoutSubItem;
            }
            else
            {
                return GetItem(item);
            }
        }

        private static MenuFlyoutItemBase GetItem(ContextMenuFlyoutItemViewModel i)
        {
            if (i.BitmapIcon != null)
            {
                var item = new MenuFlyoutItemWithImage()
                {
                    Text = i.Text,
                    Tag = i.Tag,
                    Command = i.Command,
                    CommandParameter = i.CommandParameter,
                };
                try
                {
                    item.BitmapIcon = i.BitmapIcon;
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
                return item;
            }
            MenuFlyoutItemBase flyoutItem;

            if (i.ItemType == ItemType.Toggle)
            {
                flyoutItem = new ToggleMenuFlyoutItem()
                {
                    Text = i.Text,
                    Tag = i.Tag,
                    Command = i.Command,
                    CommandParameter = i.CommandParameter,
                    IsChecked = i.IsChecked,
                };
            }
            else
            {
                var icon = string.IsNullOrEmpty(i.Glyph) ? null : new FontIcon
                {
                    Glyph = i.Glyph,
                };

                if (icon != null && !string.IsNullOrEmpty(i.GlyphFontFamilyName))
                {
                    var fontFamily = App.Current.Resources[i.GlyphFontFamilyName] as FontFamily;
                    icon.FontFamily = fontFamily;
                }

                flyoutItem = new MenuFlyoutItem()
                {
                    Text = i.Text,
                    Tag = i.Tag,
                    Command = i.Command,
                    CommandParameter = i.CommandParameter,
                    Icon = icon,
                };
            }

            if (i.KeyboardAccelerator != null)
            {
                flyoutItem.KeyboardAccelerators.Add(i.KeyboardAccelerator);
            }
            flyoutItem.IsEnabled = i.IsEnabled;

            return flyoutItem;
        }

        private static ICommandBarElement GetCommandBarItem(ContextMenuFlyoutItemViewModel item)
        {
            return item.ItemType switch
            {
                ItemType.Separator => new AppBarSeparator()
                {
                    Tag = item.Tag,
                    Visibility = item.IsHidden ? Visibility.Collapsed : Visibility.Visible,
                },
                _ => GetCommandBarButton(item),
            };
        }

        private static ICommandBarElement GetCommandBarButton(ContextMenuFlyoutItemViewModel item)
        {
            ICommandBarElement element;
            FontIcon icon = null;
            if (!string.IsNullOrEmpty(item.Glyph))
            {
                icon = new FontIcon
                {
                    Glyph = item.Glyph,
                };
            }

            if (!string.IsNullOrEmpty(item.GlyphFontFamilyName))
            {
                var fontFamily = App.Current.Resources[item.GlyphFontFamilyName] as FontFamily;
                icon.FontFamily = fontFamily;
            }

            MenuFlyout ctxFlyout = null;
            if (item.Items.Count > 0 || item.ID == "ItemOverflow")
            {
                ctxFlyout = new MenuFlyout();
                GetMenuFlyoutItemsFromModel(item.Items).ForEach(i => ctxFlyout.Items.Add(i));
            }

            UIElement content = null;
            if (item.BitmapIcon != null)
            {
                content = new Image()
                {
                    Source = item.BitmapIcon,
                };
            } else if(item.ColoredIcon.IsValid)
            {
                content = item.ColoredIcon.ToColoredIcon();
            } else if(item.ShowLoadingIndicator)
            {
                content = new Microsoft.UI.Xaml.Controls.ProgressRing()
                {
                    IsIndeterminate = true,
                    IsActive = true,
                };
            }

            if (item.ItemType == ItemType.Toggle)
            {
                element = new AppBarToggleButton()
                {
                    Label = item.Text,
                    Tag = item.Tag,
                    Command = item.Command,
                    CommandParameter = item.CommandParameter,
                    IsChecked = item.IsChecked,
                    Content = content,
                    LabelPosition = item.CollapseLabel ? CommandBarLabelPosition.Collapsed : CommandBarLabelPosition.Default,
                    IsEnabled = item.IsEnabled,
                    Visibility = item.IsHidden ? Visibility.Collapsed : Visibility.Visible,
                };

                if (icon != null)
                {
                    (element as AppBarToggleButton).Icon = icon;
                }

                if (item.IsPrimary || item.CollapseLabel)
                {
                    (element as AppBarToggleButton).SetValue(ToolTipService.ToolTipProperty, item.Text);
                }
            }
            else
            {
                element = new AppBarButton()
                {
                    Label = item.Text,
                    Tag = item.Tag,
                    Command = item.Command,
                    CommandParameter = item.CommandParameter,
                    Flyout = ctxFlyout,
                    LabelPosition = item.CollapseLabel ? CommandBarLabelPosition.Collapsed : CommandBarLabelPosition.Default,
                    Content = content,
                    IsEnabled = item.IsEnabled,
                    Visibility = item.IsHidden ? Visibility.Collapsed : Visibility.Visible,
                };

                if (icon != null)
                {
                    (element as AppBarButton).Icon = icon;
                }

                if (item.IsPrimary || item.CollapseLabel)
                {
                    (element as AppBarButton).SetValue(ToolTipService.ToolTipProperty, item.Text);
                }
            }

            return element;
        }
    }
}