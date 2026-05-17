using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PhotoViewer.ViewModels;

namespace PhotoViewer;

public class ViewLocator : IDataTemplate
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2057:Unrecognized value passed to the parameter of method 'System.Type.GetType'",
        Justification = "View 类型由 Linker.xml 保留，运行期按命名约定解析 ViewModel→View。")]
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}