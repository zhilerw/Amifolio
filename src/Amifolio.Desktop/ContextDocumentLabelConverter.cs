using Microsoft.UI.Xaml.Data;

namespace Amifolio.Desktop;

public sealed class ContextDocumentLabelConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,string language)
    {
        var name=value as string??"";var candidate=name.StartsWith("candidate-",StringComparison.Ordinal);if(candidate)name=name[10..];
        var label=name switch{"overview.md"=>"概要","memory.md"=>"记忆","style.md"=>"风格","summary.md"=>"总结","sources.md"=>"来源","preferences.md"=>"通用偏好","START.md"=>"入口",_=>"其他"};return candidate?label+"候选":label;
    }
    public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();
}
