using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace Simple_Debugger_Display_Helper
{
    [DebuggerDisplay("[{ScrollTop},{ScrollBottom}] {System.IO.Path.GetFileName(InitialFilePath)}")]
    public class ScrollInfo
    {
        // Purpose of this class:
        // Save the current document and scroll position of the window to
        // restore it if the DebuggerDisplay token was on the screen before

        private const int VerticalScrollBar = 1;

        public DocumentView InitialDocView { get; private set; }
        public IVsTextManager2 TextManager { get; private set; }
        public int? ScrollTop { get; private set; }
        public int? ScrollBottom { get; private set; }

        public string InitialFilePath => InitialDocView.FilePath;

        public bool ContainsLine(int lineNumber)
        {
            if (!ScrollTop.HasValue || !ScrollBottom.HasValue)
                return false;
            else
                return ScrollTop.Value <= lineNumber && lineNumber <= ScrollBottom.Value;
            
        }

        private const int DirectionUp = -1, DirectionDown = 1;

        public static async Task<ScrollInfo> NewScrollInfoAsync()
        {
            var initialDocView = await VS.Documents.GetActiveDocumentViewAsync();
            var textManager = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager2;
            
            ScrollInfo info = new ScrollInfo(initialDocView, textManager);
            return info;
        }

        private ScrollInfo(DocumentView initialDocView, IVsTextManager2 textManager)
        {
            InitialDocView = initialDocView;
            TextManager = textManager;

            //var test1 = await VS.Windows.GetCurrentWindowAsync();            

            if (GetActiveViewInfo(out var view, out int top))
            {
                ScrollTop = top;
                ScrollBottom = GetScrollBottom(initialDocView, top);
                //view.SetScrollPosition(VerticalScrollBar, top + (DirectionDown * i));
            }
        }

        private static int GetScrollBottom(DocumentView currentDocView, int top)
        {
            int height = currentDocView.TextView.TextViewLines.Count;
            int bottom = top + height;
            return bottom;
        }

        private bool GetActiveViewInfo(out IVsTextView view, out int top)
        {
            top = -1;
            bool ret = (TextManager.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftCodeWindow, out view) == VSConstants.S_OK)
                && (view.GetScrollInfo(VerticalScrollBar, out int _, out int _, out int _, out top) == VSConstants.S_OK);
                //&& (view.GetCaretPos(out int caretLine, out int caretColumn) == VSConstants.S_OK)
            return ret;
        }

        public void RestoreScroll()
        {
            if(GetActiveViewInfo(out var view, out int top))
                view.SetScrollPosition(VerticalScrollBar, ScrollTop.Value);
        }
    }
}
