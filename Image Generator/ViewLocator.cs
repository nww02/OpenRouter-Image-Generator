using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Mvvm.Controls;
using Image_Generator.ViewModels;

namespace Image_Generator;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        // Map ViewModels.* namespace → Views.* (handles sub-namespaces like Documents.*)
        var name = data.GetType().FullName!;
        name = name.Replace("ViewModel", "View", StringComparison.Ordinal);
        name = name.Replace(".ViewModels.", ".Views.", StringComparison.Ordinal);

        var type = Type.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            control.DataContext = data;
            return control;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase or Document;
    }
}