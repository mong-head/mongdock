using System.Reflection;
using Mongdock.Services;

namespace ReportTest;

/// <summary>
/// 그림용 가짜 IStatusService (소리 판 — #21 0.8.2): 정해 둔 값만 돌려주고 나머지는 기본값. 실제 장치·볼륨은 건드리지 않음.
/// DispatchProxy 라 인터페이스가 바뀌어도 고칠 것 없음 (이벤트 붙이기·떼기는 무시).
/// </summary>
public class FakeStatus : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = new();

    public static IStatusService Create(Action<Dictionary<string, object?>> set)
    {
        var proxy = Create<IStatusService, FakeStatus>();
        set(((FakeStatus)(object)proxy).Values);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) return null;
        string name = method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name[4..] : method.Name;
        if (Values.TryGetValue(name, out var v)) return v;
        var t = method.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = t.GetGenericArguments()[0];
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
            return fromResult.Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
        }
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}
