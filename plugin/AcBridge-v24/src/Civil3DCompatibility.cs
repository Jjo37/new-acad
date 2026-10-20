using System.Collections.Concurrent;
using System.Reflection;

namespace Civil3DMcpPlugin;

/// <summary>
/// Narrow compatibility boundary for Civil 3D members that are unavailable in
/// the referenced managed API or vary between supported host versions.
/// Documented Autodesk members should be called directly instead.
/// </summary>
internal static class Civil3DCompatibility
{
  internal readonly record struct ParameterShape(Type Type);
  private sealed record CachedProperty(PropertyInfo? Value);
  private sealed record CachedMethods(MethodInfo[] Values);
  private sealed record CachedField(FieldInfo? Value);
  private sealed record CachedType(Type? Value);

  private readonly record struct PropertyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct MethodKey(Type Type, string Name, bool IsStatic, int ArgumentCount);
  private readonly record struct MethodFamilyKey(Type Type, string Name, bool IsStatic);
  private readonly record struct LoadedStaticMethodKey(string Name, Type FirstParameterType, int ArgumentCount);

  private static readonly ConcurrentDictionary<PropertyKey, CachedProperty> PropertyCache = new();
  private static readonly ConcurrentDictionary<PropertyKey, CachedField> FieldCache = new();
  private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ScalarPropertyCache = new();
  private static readonly ConcurrentDictionary<MethodKey, CachedMethods> MethodCache = new();
  private static readonly ConcurrentDictionary<MethodFamilyKey, CachedMethods> MethodFamilyCache = new();
  private static readonly ConcurrentDictionary<LoadedStaticMethodKey, CachedMethods> LoadedStaticMethodCache = new();
  private static readonly ConcurrentDictionary<string, CachedType> TypeCache = new(StringComparer.Ordinal);

  public static T? GetPropertyValue<T>(object? target, string propertyName)
  {
    var raw = GetPropertyValue(target, propertyName);
    if (raw == null)
    {
      return default;
    }

    if (raw is T typed)
    {
      return typed;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
      if (targetType.IsInstanceOfType(raw))
      {
        return (T)(object)raw;
      }

      return (T)(object)Convert.ChangeType(raw, targetType)!;
    }
    catch
    {
      return default;
    }
  }

  public static object? GetPropertyValue(object? target, string propertyName)
  {
    if (target == null)
    {
      return null;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property == null)
    {
      return null;
    }

    try
    {
      return property.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static object? GetIndexedPropertyValue(object? target, string propertyName, params object?[] indexes)
  {
    if (target == null)
    {
      return null;
    }

    // 2026-08-16: Item 属性有多个重载（如 ParamDoubleCollection.Item[string] 和 Item[int]），
    // GetProperty(name) 会抛 AmbiguousMatchException——按索引参数类型精确匹配
    var property = ResolveIndexedProperty(target.GetType(), propertyName, indexes);
    try
    {
      return property?.GetValue(target, indexes);
    }
    catch
    {
      return null;
    }
  }

  private static PropertyInfo? ResolveIndexedProperty(Type type, string propertyName, object?[] indexes)
  {
    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.Name == propertyName && p.GetIndexParameters().Length == indexes.Length)
      .ToList();
    foreach (var p in properties)
    {
      var idxParams = p.GetIndexParameters();
      var match = true;
      for (var i = 0; i < idxParams.Length; i++)
      {
        var idxType = idxParams[i].ParameterType;
        var arg = indexes[i];
        if (arg == null) { match = false; break; }
        if (idxType == typeof(string) && arg is string) continue;
        if (idxType == typeof(int) && (arg is int || (arg is long l && l == (int)l))) continue;
        match = false;
        break;
      }
      if (match) return p;
    }
    return properties.FirstOrDefault();
  }

  public static object? GetFieldValue(object? target, string fieldName)
  {
    if (target == null)
    {
      return null;
    }

    var type = target.GetType();
    var key = new PropertyKey(type, fieldName, IsStatic: false);
    var field = FieldCache.GetOrAdd(key, static item =>
      new CachedField(item.Type.GetField(item.Name, BindingFlags.Public | BindingFlags.Instance))).Value;
    try
    {
      return field?.GetValue(target);
    }
    catch
    {
      return null;
    }
  }

  public static bool TrySetProperty(object? target, string propertyName, object? value)
  {
    if (target == null)
    {
      return false;
    }

    var property = ResolveProperty(target.GetType(), propertyName, isStatic: false);
    if (property?.CanWrite != true)
    {
      return false;
    }

    try
    {
      var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
      var converted = value == null || targetType.IsInstanceOfType(value)
        ? value
        : targetType.IsEnum && value is string enumText
          ? Enum.Parse(targetType, enumText, ignoreCase: true)
          : Convert.ChangeType(value, targetType);
      property.SetValue(target, converted);
      return true;
    }
    catch
    {
      return false;
    }
  }

  public static IReadOnlyDictionary<string, object?> GetReadableScalarProperties(object target)
  {
    var properties = ScalarPropertyCache.GetOrAdd(target.GetType(), static type =>
      type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .Where(property =>
        {
          var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
          return propertyType == typeof(string)
            || propertyType == typeof(bool)
            || propertyType == typeof(int)
            || propertyType == typeof(double);
        })
        .ToArray());

    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in properties)
    {
      try
      {
        values[property.Name] = property.GetValue(target);
      }
      catch
      {
        // Some Autodesk wrappers throw for state-dependent getters. Omit them.
      }
    }

    return values;
  }

  public static object? InvokeMethod(object? target, string methodName, params object?[] arguments)
  {
    if (target == null)
    {
      return null;
    }

    TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out var result);
    return result;
  }

  public static bool TryInvokeMethod(object? target, string methodName, out object? result, params object?[] arguments)
  {
    result = null;
    return target != null
      && TryInvokeCandidates(target.GetType(), target, methodName, isStatic: false, arguments, out result);
  }

  public static object? InvokeStaticMethod(Type type, string methodName, params object?[] arguments)
  {
    TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out var result);
    return result;
  }

  public static bool TryInvokeStaticMethod(Type type, string methodName, out object? result, params object?[] arguments)
  {
    return TryInvokeCandidates(type, null, methodName, isStatic: true, arguments, out result);
  }

  public static bool TryInvokeStaticOverloads(
    Type type,
    string methodName,
    Func<ParameterShape[], object?[]?> argumentBuilder,
    out object? result,
    out object?[]? invokedArguments)
  {
    return TryInvokeStaticOverloads(type, methodName, argumentBuilder, out result, out invokedArguments, allowRetry: false);
  }

  // 2026-08-12 M6: allowRetry=false(默认)——写操作在 TargetInvocationException 时直接抛(可能已创建一半, 重试=双重副作用)
  public static bool TryInvokeStaticOverloads(
    Type type,
    string methodName,
    Func<ParameterShape[], object?[]?> argumentBuilder,
    out object? result,
    out object?[]? invokedArguments,
    bool allowRetry)
  {
    result = null;
    invokedArguments = null;
    var key = new MethodFamilyKey(type, methodName, IsStatic: true);
    var methods = MethodFamilyCache.GetOrAdd(key, static item =>
      new CachedMethods(item.Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == item.Name)
        .OrderBy(method => method.GetParameters().Length)
        .ToArray())).Values;

    foreach (var method in methods)
    {
      var shapes = method.GetParameters()
        .Select(parameter => new ParameterShape(
          parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType))
        .ToArray();
      var arguments = argumentBuilder(shapes);
      if (arguments == null)
      {
        continue;
      }

      try
      {
        result = method.Invoke(null, arguments);
        invokedArguments = arguments;
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
      catch (TargetInvocationException tie)
      {
        // 2026-08-12 M6: 写操作(allowRetry=false)直接抛——可能已创建对象一半, 重试=双重副作用
        if (!allowRetry)
        {
          System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException ?? tie).Throw();
        }
        // 读操作可继续尝试另一重载
      }
    }

    return false;
  }

  public static bool TryInvokeLoadedStaticMethod(
    string methodName,
    Type firstParameterType,
    out object? result,
    params object?[] arguments)
  {
    result = null;
    var key = new LoadedStaticMethodKey(methodName, firstParameterType, arguments.Length);
    var methods = LoadedStaticMethodCache.GetOrAdd(key, static item =>
    {
      var matches = new List<MethodInfo>();
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        Type[] types;
        try
        {
          types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
          types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
          continue;
        }

        foreach (var type in types)
        {
          matches.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == item.Name)
            .Where(method => method.GetParameters().Length == item.ArgumentCount)
            .Where(method => method.GetParameters().Length > 0
              && method.GetParameters()[0].ParameterType.IsAssignableFrom(item.FirstParameterType)));
        }
      }
      return new CachedMethods(matches.ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(null, arguments);
        return true;
      }
      catch (ArgumentException)
      {
      }
      catch (TargetParameterCountException)
      {
      }
    }
    return false;
  }

  public static Type? FindLoadedType(params string[] fullNames)
  {
    foreach (var fullName in fullNames)
    {
      var cached = TypeCache.GetOrAdd(fullName, static candidateName =>
      {
        var assemblyQualifiedType = Type.GetType(candidateName, throwOnError: false, ignoreCase: false);
        if (assemblyQualifiedType != null)
        {
          return new CachedType(assemblyQualifiedType);
        }

        var commaIndex = candidateName.IndexOf(',');
        var normalizedName = commaIndex >= 0
          ? candidateName[..commaIndex].Trim()
          : candidateName;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
          var type = assembly.GetType(normalizedName, throwOnError: false, ignoreCase: false);
          if (type != null)
          {
            return new CachedType(type);
          }
        }

        return new CachedType(null);
      });
      if (cached.Value != null)
      {
        return cached.Value;
      }
    }

    return null;
  }

  private static PropertyInfo? ResolveProperty(Type type, string propertyName, bool isStatic)
  {
    var key = new PropertyKey(type, propertyName, isStatic);
    return PropertyCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      var property = item.Type.GetProperty(item.Name, flags);
      // 2026-08-16: 子类同名属性 getter 非公开时（如 StyleBase.Name {get=私有}），
      // 沿继承链找公开 getter 的版本（如 SymbolTableRecord.Name）——否则 GetValue 抛异常返回 null
      if (property != null && property.GetMethod?.IsPublic != true)
      {
        var current = item.Type.BaseType;
        while (current != null)
        {
          var baseProp = current.GetProperty(item.Name, flags);
          if (baseProp != null && baseProp.GetMethod?.IsPublic == true)
          {
            property = baseProp;
            break;
          }
          current = current.BaseType;
        }
      }
      return new CachedProperty(property);
    }).Value;
  }

  private static bool TryInvokeCandidates(
    Type type,
    object? target,
    string methodName,
    bool isStatic,
    object?[] arguments,
    out object? result)
  {
    result = null;
    var key = new MethodKey(type, methodName, isStatic, arguments.Length);
    var methods = MethodCache.GetOrAdd(key, static item =>
    {
      var flags = BindingFlags.Public | (item.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
      return new CachedMethods(item.Type
        .GetMethods(flags)
        .Where(method => method.Name == item.Name && method.GetParameters().Length == item.ArgumentCount)
        .ToArray());
    }).Values;

    foreach (var method in methods)
    {
      try
      {
        result = method.Invoke(target, arguments);
        return true;
      }
      catch (ArgumentException)
      {
        // Try the next overload. Invocation exceptions from a compatible
        // overload are allowed to propagate to the command's explicit error path.
      }
      catch (TargetParameterCountException)
      {
      }
    }

    return false;
  }
}
