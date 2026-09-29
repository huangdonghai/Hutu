using System.Reflection;

namespace Sample.Common;

public static class SampleUtils
{
    public static Assembly GetSampleAssembly()
    {
        return typeof(SampleUtils).Assembly;
    }
}
