using System.Text.RegularExpressions;
using System.Text;

if (args.Length < 1)
{
    Console.WriteLine("Usage: StripCommentsTool <directory>");
    return;
}

var dir = args[0];
var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

foreach (var file in files)
{
    var text = File.ReadAllText(file);
    var original = text;

    // remove multi-line comments but keep XML doc comments
    text = Regex.Replace(text, @"/\*(?!\*).*?\*/", "", RegexOptions.Singleline);
    // remove single-line comments except XML doc
    text = Regex.Replace(text, "(^|\s)//(?!/).*?$", "", RegexOptions.Multiline);

    if (text != original)
    {
        File.WriteAllText(file, text, Encoding.UTF8);
        Console.WriteLine($"Stripped comments from {file}");
    }
}
