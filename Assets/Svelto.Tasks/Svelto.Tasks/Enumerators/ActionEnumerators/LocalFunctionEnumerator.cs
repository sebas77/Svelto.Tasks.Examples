using System;
using System.Collections;
using System.Collections.Generic;

namespace Svelto.Tasks.Enumerators
{
  public struct LocalFunctionEnumerator : IEnumerator<TaskContract>
  {
    public LocalFunctionEnumerator(Func<bool> func) : this()
    {
      _func = func;
    }

    public bool MoveNext()
    {
      return _func();
    }

    public void Reset()
    {}

    TaskContract IEnumerator<TaskContract>.Current => Yield.It;

    object IEnumerator.Current => null;

    public override string ToString()
    {
      if (_name == null)
      {
        var method = _func.GetMethodInfoEx();

        _name = method.GetDeclaringType().Name.FastConcat(".", method.Name);
      }

      return _name;
    }

    public void Dispose()
    {}

    readonly Func<bool> _func;
    string              _name;
  }
}