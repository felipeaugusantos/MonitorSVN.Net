using System.IO.Compression;

namespace MonitorSVN.Services;

/// <summary>
/// Confere se cada ZIP da pasta do projeto tem o nome do arquivo que contem: "autcom.zip" deve guardar
/// "autcom.exe". Mesma regra do VarreduraSistema (validate_zip_names). Retorna a lista de problemas (vazia = ok).
/// </summary>
public static class ZipNameValidator
{
    private static readonly string[] Supported = { ".exe", ".dll", ".bpl" };

    public static List<string> Validate(string projectPath)
    {
        var errors = new List<string>();
        var zips = Directory.EnumerateFiles(projectPath, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var zipPath in zips)
        {
            var zipName = Path.GetFileName(zipPath);
            List<string> members;
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                members = archive.Entries
                    .Where(e => e.Name.Length > 0
                        && Supported.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)
                        && !e.Name.StartsWith("err_", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name)
                    .ToList();
            }
            catch (InvalidDataException)
            {
                errors.Add($"{zipName}: ZIP invalido ou corrompido");
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{zipName}: erro ao ler ZIP ({ex.Message})");
                continue;
            }

            // ZIP com varios executaveis (ou nenhum) nao tem nome "certo" a exigir
            if (members.Count != 1) continue;

            var expected = Path.GetFileNameWithoutExtension(members[0]) + ".zip";
            if (!string.Equals(zipName, expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{zipName} contem {members[0]}; esperado {expected}");
        }
        return errors;
    }
}
