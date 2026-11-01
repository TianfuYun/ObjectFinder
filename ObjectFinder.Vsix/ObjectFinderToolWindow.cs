using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;

namespace ObjectFinder.Vsix
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// </remarks>
    [Guid("4e7cfdaf-f201-43e9-87d2-3d7adf77b765")]
    public class ObjectFinderToolWindow : ToolWindowPane
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectFinderToolWindow"/> class.
        /// </summary>
        public ObjectFinderToolWindow() : base(null)
        {
            this.Caption = "Object Finder";

            // This is the user control hosted by the tool window; Note that, even if this class implements IDisposable,
            // we are not calling Dispose on this object. This is because ToolWindowPane calls Dispose on
            // the object returned by the Content property.
            var view = new SearchView();
            view.CloseRequested += HideWindow;
            Content = view;
        }

        private void HideWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IVsWindowFrame frame = Frame as IVsWindowFrame;
            if (frame != null)
                frame.Hide();
        }
    }
}
