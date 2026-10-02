using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SoftParam.Tests;

/// <summary>
/// D0: which native web view Rhino 7's Eto creates for Eto.Forms.WebView,
/// read from the IL of the installed Eto.XamMac2.dll. Rhino is not launched.
/// The platform registers WKWebViewHandler by default; the old WebKit.WebView
/// handler only comes in through Platform.UseWebView, which no Rhino assembly
/// calls. Nothing to read on a machine without Rhino 7.
/// </summary>
public class EtoWebViewTests
{
    const string Resources = "/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources";
    readonly ITestOutputHelper _out;

    public EtoWebViewTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public void RhinoEto_CreatesAWKWebView()
    {
        var eto = Path.Combine(Resources, "Eto.XamMac2.dll");
        if (!File.Exists(eto))
        {
            _out.WriteLine("Rhino 7 is not installed here: no Eto assembly to read.");
            return;
        }
        var il = new EtoIl(eto);
        var made = il.Constructed();

        Assert.Contains(made, m => m.Method.Contains("<AddTo>") && m.Type == "Eto.Mac.Forms.Controls.WKWebViewHandler");
        var legacy = made.Where(m => m.Type == "Eto.Mac.Forms.Controls.WebViewHandler").ToList();
        Assert.NotEmpty(legacy);
        Assert.All(legacy, m => Assert.Contains("<UseWebView>", m.Method));

        var callers = Directory.GetFiles(Resources, "*.dll")
            .Where(f => Path.GetFileName(f) != "Eto.XamMac2.dll" && Mentions(f, "UseWebView"))
            .ToList();
        Assert.Empty(callers);

        var native = il.WebKitBase("Eto.Mac.Forms.Controls.WKWebViewHandler+EtoWebView");
        Assert.Equal("WebKit.WKWebView", native);
        _out.WriteLine("native web view: Eto.Mac.Forms.Controls.WKWebViewHandler+EtoWebView : " + native);
    }

    static bool Mentions(string file, string text)
    {
        var bytes = File.ReadAllBytes(file);
        var needle = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i + needle.Length <= bytes.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && bytes[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    /// <summary>Just enough IL reading to list the objects each method constructs.</summary>
    sealed class EtoIl
    {
        public sealed record Made(string Method, string Type);

        readonly PEReader _pe;
        readonly MetadataReader _md;
        static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes).GetFields()
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

        public EtoIl(string path)
        {
            _pe = new PEReader(File.OpenRead(path));
            _md = _pe.GetMetadataReader();
        }

        public List<Made> Constructed()
        {
            var list = new List<Made>();
            foreach (var th in _md.TypeDefinitions)
            {
                foreach (var mh in _md.GetTypeDefinition(th).GetMethods())
                {
                    var method = _md.GetMethodDefinition(mh);
                    if (method.RelativeVirtualAddress == 0) continue;
                    var name = Name(th) + "::" + _md.GetString(method.Name);
                    var il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                    while (il.RemainingBytes > 0)
                    {
                        short value = il.ReadByte();
                        if (value == 0xFE) value = (short)(0xFE00 | il.ReadByte());
                        if (!Ops.TryGetValue(value, out var op)) break;
                        var token = Operand(ref il, op.OperandType);
                        if (op != OpCodes.Newobj || token == 0) continue;
                        var ctor = MetadataTokens.EntityHandle(token);
                        if (ctor.Kind == HandleKind.MethodDefinition)
                            list.Add(new Made(name, Name(_md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType())));
                        else if (ctor.Kind == HandleKind.MemberReference)
                            list.Add(new Made(name, Name(_md.GetMemberReference((MemberReferenceHandle)ctor).Parent)));
                    }
                }
            }
            return list;
        }

        /// <summary>The first WebKit type a type derives from.</summary>
        public string WebKitBase(string typeName)
        {
            foreach (var th in _md.TypeDefinitions)
            {
                if (Name(th) != typeName) continue;
                var baseType = _md.GetTypeDefinition(th).BaseType;
                return baseType.IsNil ? null : Name(baseType);
            }
            return null;
        }

        static int Operand(ref BlobReader il, OperandType type)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: il.ReadByte(); return 0;
                case OperandType.InlineVar: il.ReadInt16(); return 0;
                case OperandType.InlineI8:
                case OperandType.InlineR: il.ReadInt64(); return 0;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    for (var i = 0; i < count; i++) il.ReadInt32();
                    return 0;
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.InlineField: return il.ReadInt32();
                default: il.ReadInt32(); return 0;
            }
        }

        string Name(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var t = _md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var outer = t.GetDeclaringType();
                    var ns = _md.GetString(t.Namespace);
                    var prefix = !outer.IsNil ? Name(outer) + "+" : ns.Length > 0 ? ns + "." : "";
                    return prefix + _md.GetString(t.Name);
                }
                case HandleKind.TypeReference:
                {
                    var t = _md.GetTypeReference((TypeReferenceHandle)handle);
                    var ns = _md.GetString(t.Namespace);
                    return (ns.Length > 0 ? ns + "." : "") + _md.GetString(t.Name);
                }
                default:
                    return handle.Kind.ToString();
            }
        }
    }
}
