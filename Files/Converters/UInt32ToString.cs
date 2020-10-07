﻿using System;
using Windows.UI.Xaml.Data;

namespace Files.Converters
{
    internal class UInt32ToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value != null)
                return value.ToString();
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            try
            {
                return UInt32.Parse(value as string);
            }
            catch (FormatException e)
            {
                return null;
            }
        }
    }
}