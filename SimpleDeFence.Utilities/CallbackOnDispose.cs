using SimpleDeFence.Utilities;
using System;

namespace SimpleDeFence.Utilities
{
    public sealed class CallbackOnDispose : Disposable
    {
        private readonly Action Callback;

        public CallbackOnDispose(Action onDispose)
        {
            Callback = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            Callback();

            base.Dispose(disposing);
        }
    }
}