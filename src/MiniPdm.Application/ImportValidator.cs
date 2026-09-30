using MiniPdm.Domain;

namespace MiniPdm.Application;

public static class ImportValidator
{
    public static ValidatedBatch Validate(IReadOnlyList<CadReadResult> reads)
    {
        var byName = reads.ToDictionary(r => r.FileName, StringComparer.Ordinal);
        var errors = reads.ToDictionary(r => r.FileName, _ => new List<string>(), StringComparer.Ordinal);
        var docs = reads.Where(r => r.Document is not null)
            .ToDictionary(r => r.FileName, r => r.Document!, StringComparer.Ordinal);

        foreach (var result in reads)
        {
            if (result.Document is null)
            {
                errors[result.FileName].Add(result.Error ?? "Не удалось прочитать документ");
                continue;
            }
            var document = result.Document;
            if (document.FormatVersion != 1) errors[result.FileName].Add("Неподдерживаемая версия формата");
            if (document.FileName != result.FileName) errors[result.FileName].Add("Имя файла внутри документа не совпадает с именем на диске");
            if (string.IsNullOrWhiteSpace(document.Name)) errors[result.FileName].Add("Не указано наименование");
            if (!Enum.IsDefined(document.Kind)) errors[result.FileName].Add("Неизвестный тип объекта");
            if (document.Kind == CadKind.Assembly && !result.FileName.EndsWith(".a3d", StringComparison.OrdinalIgnoreCase)
                || document.Kind != CadKind.Assembly && !result.FileName.EndsWith(".m3d", StringComparison.OrdinalIgnoreCase))
                errors[result.FileName].Add("Расширение файла не соответствует типу объекта");
            if (document.Kind is CadKind.Assembly or CadKind.Part)
            {
                if (!Designation.IsValid(document.Designation)) errors[result.FileName].Add("Неверный формат обозначения");
            }
            else if (document.Designation is not null) errors[result.FileName].Add("Стандартное изделие не должно иметь обозначения");
            if (document.Kind == CadKind.Assembly)
            {
                if (document.Material is not null || document.MassKg is not null)
                    errors[result.FileName].Add("У сборки не задают материал и собственную массу");
                foreach (var component in document.Components)
                {
                    if (component.Quantity <= 0) errors[result.FileName].Add($"Количество компонента «{component.FileName}» должно быть положительным");
                    if (!byName.ContainsKey(component.FileName)) errors[result.FileName].Add($"Отсутствует файл «{component.FileName}»");
                }
                foreach (var group in document.Components.GroupBy(component => component.FileName, StringComparer.Ordinal))
                {
                    var total = 0;
                    foreach (var component in group)
                    {
                        if (component.Quantity <= 0) continue;
                        if (total > int.MaxValue - component.Quantity)
                        {
                            errors[result.FileName].Add($"Суммарное количество компонента «{group.Key}» превышает допустимый предел");
                            break;
                        }
                        total += component.Quantity;
                    }
                }
            }
            else
            {
                if (document.Components.Count > 0) errors[result.FileName].Add("Деталь не должна содержать компоненты");
                if (document.Kind == CadKind.Part && string.IsNullOrWhiteSpace(document.Material)) errors[result.FileName].Add("Не указан материал детали");
                if (document.Kind == CadKind.StandardPart && document.Material is not null) errors[result.FileName].Add("Стандартному изделию не задают материал");
                if (document.Kind == CadKind.StandardPart && document.MassKg is null) errors[result.FileName].Add("Не указана масса стандартного изделия");
                if (document.MassKg < 0) errors[result.FileName].Add("Масса не может быть отрицательной");
            }
        }

        foreach (var group in docs.Values.Where(d => d.Kind is CadKind.Assembly or CadKind.Part)
                     .GroupBy(d => d.Designation, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var document in group) errors[document.FileName].Add($"Обозначение «{group.Key}» повторяется в пакете");
        foreach (var group in docs.Values.Where(d => d.Kind == CadKind.StandardPart)
                     .GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var document in group) errors[document.FileName].Add($"Наименование «{group.Key}» повторяется в пакете");

        var visit = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        void Walk(string name)
        {
            visit[name] = 1;
            stack.Add(name);
            foreach (var child in docs[name].Components.Select(c => c.FileName))
            {
                if (!docs.TryGetValue(child, out var target) || target.Kind != CadKind.Assembly) continue;
                if (!visit.TryGetValue(child, out var state)) Walk(child);
                else if (state == 1)
                {
                    var start = stack.IndexOf(child);
                    var cycle = stack.Skip(start).Append(child);
                    var reason = "Цикл состава: " + string.Join(" → ", cycle);
                    foreach (var member in stack.Skip(start))
                        if (!errors[member].Contains(reason)) errors[member].Add(reason);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            visit[name] = 2;
        }
        foreach (var document in docs.Values.Where(d => d.Kind == CadKind.Assembly))
            if (!visit.ContainsKey(document.FileName)) Walk(document.FileName);

        bool changed;
        do
        {
            changed = false;
            foreach (var document in docs.Values.Where(d => d.Kind == CadKind.Assembly && errors[d.FileName].Count == 0))
            {
                var rejected = document.Components.FirstOrDefault(c => errors.TryGetValue(c.FileName, out var reasons) && reasons.Count > 0);
                if (rejected is null) continue;
                errors[document.FileName].Add($"Компонент «{rejected.FileName}» отклонён");
                changed = true;
            }
        } while (changed);

        var rows = reads.Select(r =>
        {
            var document = r.Document;
            if (errors[r.FileName].Count > 0) return new ImportRow(r.FileName, ImportSeverity.Error, string.Join("; ", errors[r.FileName]));
            if (document!.Kind == CadKind.Part && document.MassKg is null)
                return new ImportRow(r.FileName, ImportSeverity.Warning, "Не указана масса детали");
            return new ImportRow(r.FileName, ImportSeverity.Success, "Принято");
        }).ToArray();
        var accepted = reads.Where(r => errors[r.FileName].Count == 0).Select(r => r.Document!).ToArray();
        return new ValidatedBatch(accepted, rows);
    }
}
