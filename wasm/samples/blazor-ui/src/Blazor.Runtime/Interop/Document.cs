﻿namespace Blazor.Runtime.Interop
{
    public class Document : JavaScriptInvocation
    {
        internal Document(JSObjectHandle currentHandle) : base(currentHandle)
        {
        }

        public Node GetElementById(string id)
        {
            var elem = this["getElementById"].Invoke<JSObjectHandle>(id);
            return elem == null ? null : new Node(elem);
        }

        public class Node : JSObject
        {
            public Node(JSObjectHandle handle) : base(handle) { }

            public string TagName => (string)GetProperty("tagName");
        }
    }
}
