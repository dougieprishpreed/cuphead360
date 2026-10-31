// Reference oracle for selected pure EaseUtils methods.
//
// Build with the .NET Framework 4.0 csc.exe and point it at the user's
// Assembly-CSharp.dll. This executes the original managed IL on the desktop
// CLR; it is a behavioral oracle for the Cuphead360 interpreter, not part of
// the shipped game.
//
// Usage: EaseReference.exe <path-to-Assembly-CSharp.dll>

using System;
using System.Globalization;
using System.Reflection;

internal static class EaseReference
{
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string name = new AssemblyName(args.Name).Name + ".dll";
        string path = System.IO.Path.Combine(dir, name);
        if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path);
        return null;
    }

    private static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: EaseReference <Assembly-CSharp.dll>");
            Environment.Exit(2);
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Assembly asm = Assembly.LoadFrom(args[0]);
        Type ease = asm.GetType("EaseUtils");
        if (ease == null)
        {
            Console.Error.WriteLine("EaseUtils not found");
            Environment.Exit(1);
        }

        string[] names = {
            "EaseInQuad", "EaseOutQuad", "EaseInOutQuad",
            "EaseInCubic", "EaseOutCubic", "EaseInOutCubic"
        };
        float[] a = { 0.0f, 0.0f, 0.25f };
        float[] b = { 10.0f, 10.0f, 10.0f };
        float[] t = { 0.25f, 0.5f, 0.9f };

        for (int n = 0; n < names.Length; ++n)
        {
            MethodInfo m = ease.GetMethod(names[n], BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                Console.WriteLine(names[n] + " NOT_FOUND");
                continue;
            }
            for (int i = 0; i < a.Length; ++i)
            {
                object r = m.Invoke(null, new object[] { a[i], b[i], t[i] });
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}({1:R},{2:R},{3:R})={4:R}", names[n], a[i], b[i], t[i],
                    Convert.ToSingle(r)));
            }
        }

        MethodInfo dispatch = ease.GetMethod("Ease", BindingFlags.Public | BindingFlags.Static);
        if (dispatch != null)
        {
            Type enumType = ease.GetNestedType("EaseType");
            for (int e = 0; e < 3; ++e)
            {
                object ev = Enum.ToObject(enumType, e);
                object r = dispatch.Invoke(null, new object[] { ev, 0.0f, 10.0f, 0.25f });
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Ease(enum={0},0,10,0.25)={1:R}", e, Convert.ToSingle(r)));
            }
        }
    }
}
