using System.Collections;
using System.Collections.Generic;
using Svelto.Utilities;

namespace Svelto.Tasks.Enumerators
{
    public struct FastSmartFunctionEnumerator<TVal>: IEnumerator<TaskContract>
    {
        public FastSmartFunctionEnumerator(FuncRef<TVal, bool> func):this()
        {
            _func  = func;
            _value = default(TVal);
        }

        public bool MoveNext()
        {
            return _func(ref _value);
        }

        public void Reset()
        {}

        TaskContract IEnumerator<TaskContract>.Current
        {
            get { return Yield.It; }
        }

        public TVal Current
        {
            get { return _value; }
        }

        object IEnumerator.Current
        {
            get { return null; }
        }
        
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

        readonly FuncRef<TVal, bool> _func;
        TVal                         _value;
        string                       _name;
    }
}