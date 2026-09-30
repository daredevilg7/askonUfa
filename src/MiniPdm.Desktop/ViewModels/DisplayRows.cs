using System.Collections.ObjectModel;
using MiniPdm.Application;

namespace MiniPdm.Desktop.ViewModels;

public sealed class TreeNodeViewModel
{
    public TreeNodeViewModel(BomNode data)
    {
        Data = data;
        Label = data.Depth == 0
            ? $"{data.Designation}  {data.Name}"
            : $"{data.Designation ?? "СТАНДАРТ"}  {data.Name}  × {data.DirectQuantity}";
    }

    public BomNode Data { get; }
    public string Label { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];
}

public sealed record ReportRowViewModel(string FileName, string Result, string Reason)
{
    public static ReportRowViewModel From(ImportRow row) => new(row.FileName,
        row.Severity switch
        {
            ImportSeverity.Success => "Принято",
            ImportSeverity.Warning => "Предупреждение",
            _ => "Ошибка"
        }, row.Reason);
}

public sealed record SpecificationRowViewModel(string Designation, string Name, long Quantity, string UnitMass, string LineMass)
{
    public static SpecificationRowViewModel From(SpecificationLine row) => new(
        row.Designation ?? "—", row.Name, row.Quantity,
        row.UnitMassKg?.ToString("0.####") ?? "—",
        row.TotalMassKg?.ToString("0.####") ?? "—");
}
