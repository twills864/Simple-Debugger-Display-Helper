using Community.VisualStudio.Toolkit;
using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Simple_Debugger_Display_Helper
{
    [Command(PackageIds.AddToDebuggerDisplay)]
    internal sealed class AddToDebuggerDisplay : BaseCommand<AddToDebuggerDisplay>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await AddToDebuggerDisplayAsync();
        }

        public static async Task AddToDebuggerDisplayAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DTE service = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;

            //bool wentToDefinition = await GoToDefinitionAsync(service);
            await GoToDefinitionAsync(service);
            await AddOrAppendToDebuggerDisplayAsync(service);
        }


        /*
         * 1. Get the global IVsObjectManager2 interface (implemented by the SVsObjectManager object)
           2. Call IVsObjectManager2.FindLibrary to get the C# library, and cast the result to IVsSimpleLibrary2.
           3. Call IVsSimpleLibrary2.GetList2 with the correct VSOBSEARCHCRITERIA2 in order to locate the symbol within the projects for your solution.
               a. If the resulting IVsSimpleObjectList2 has GetItemCount()==1, and CanGoToSource with VSOBJGOTOSRCTYPE.GS_DEFINITION returns pfOK==true, use the GoToSource method to jump to the source.
               b. Otherwise, rather than jumping to the source, simply display the possible options to the user. You will be able to use the IVsFindSymbol interface (implemented by the SVsObjectSearch object) to for this.
         */
        /// <summary>
        /// TODO: Handle partial class declarations (see above comments)
        /// </summary>
        /// <returns></returns>
        private static async Task GoToDefinitionAsync(DTE service)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            service.ExecuteCommand("Edit.GoToDefinition");
        }

        #region Add or Append

        private static async Task AddOrAppendToDebuggerDisplayAsync(DTE service)
        {
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            ITextBuffer textBuffer = docView.TextBuffer;
            ITextSnapshot snapshot = textBuffer.CurrentSnapshot;
            string allText = snapshot.GetText();

            SnapshotPoint caretPosition = docView.TextView.Caret.Position.BufferPosition;

            SyntaxTree tree = CSharpSyntaxTree.ParseText(allText);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
            SyntaxToken caretToken = GetTokenUnderCaret(root, ref caretPosition);

            if (!IsValidToken(caretToken))
                return;

            ClassDeclarationSyntax containingClassDeclaration = GetContainingClassDeclaration(caretToken);
            if (containingClassDeclaration == null)
                return;

            ITrackingPoint classTrackingPoint = snapshot.CreateTrackingPoint(containingClassDeclaration.SpanStart, PointTrackingMode.Positive);
            ITextEdit edit = textBuffer.CreateEdit();

            if (!HasDebuggerDisplayAttribute(containingClassDeclaration))
                AddDebuggerDisplay(edit, allText, containingClassDeclaration, caretToken);
            else
                AppendToDebuggerDisplay(edit, allText, containingClassDeclaration, caretToken);

            AddDiagnosticsToUsings(edit, root);

            // TODO Maybe: Check for DebuggerDisplay private method and add it if it's not present

            edit.Apply();

            await SetCaretToDebuggerDisplayAsync(textBuffer, classTrackingPoint, service);
        }

        #region Roslyn functionality

        private static SyntaxToken GetTokenUnderCaret(CompilationUnitSyntax root, ref SnapshotPoint caretPosition)
        {
            SyntaxToken caretToken = root.FindToken(caretPosition);

            // Field or left paren - move caret back one to capture name
            if (caretToken.IsKind(SyntaxKind.SemicolonToken)
                || caretToken.IsKind(SyntaxKind.OpenParenToken))
            {
                caretPosition = caretPosition.Subtract(1);
                caretToken = root.FindToken(caretPosition);
            }

            return caretToken;
        }

        private static bool IsValidToken(SyntaxToken token)
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
                return false;

            SyntaxKind parentKind = token.Parent.Kind();
            switch (parentKind)
            {
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.MethodDeclaration:
                    return true;
                // Field declarations are denoted as VariableDeclarators
                case SyntaxKind.VariableDeclarator:
                    var variableDeclaratorParent = token.Parent;
                    var variableDeclarationParent = variableDeclaratorParent?.Parent;
                    var fieldDeclarationParent = variableDeclarationParent?.Parent;

                    SyntaxKind? kind = fieldDeclarationParent?.Kind();
                    bool isFieldDeclaration = kind == SyntaxKind.FieldDeclaration;
                    return isFieldDeclaration;
                default:
                    return false;
            }
        }

        private static ClassDeclarationSyntax GetContainingClassDeclaration(SyntaxToken hoverToken)
        {
            SyntaxNode classDefinitionNode = hoverToken.Parent;

            while (classDefinitionNode != null && !classDefinitionNode.IsKind(SyntaxKind.ClassDeclaration))
                classDefinitionNode = classDefinitionNode.Parent;

            ClassDeclarationSyntax classDeclaration = classDefinitionNode as ClassDeclarationSyntax;
            return classDeclaration;
        }

        #endregion

        private static bool HasDebuggerDisplayAttribute(ClassDeclarationSyntax classDeclaration)
        {
            List<AttributeSyntax> attributes = classDeclaration.AttributeLists
                .SelectMany(x => x.ChildNodes().Where(y => y.IsKind(SyntaxKind.Attribute)))
                .Select(x => x as AttributeSyntax)
                .ToList();

            AttributeSyntax debuggerDisplay = attributes.Where(x => x.Name.ToString().StartsWith("DebuggerDisplay")).FirstOrDefault();
            bool hasDebuggerDisplay = debuggerDisplay != null;
            return hasDebuggerDisplay;
        }

        private static void AddDebuggerDisplay(ITextEdit edit, string allText, ClassDeclarationSyntax classDeclaration, SyntaxToken caretToken)
        {
            int whitespaceCount = 0;
            for (int i = classDeclaration.SpanStart - 1; i >= 0 && allText[i] != '\n'; i--)
                whitespaceCount++;

            int debuggerDisplayStart = classDeclaration.SpanStart;
            string tokenText = GetTokenText(caretToken);
            string debuggerDisplay = $"[DebuggerDisplay(\"{{{tokenText}}}\")]{Environment.NewLine}{new string(' ', whitespaceCount)}";
            edit.Insert(debuggerDisplayStart, debuggerDisplay);
        }

        private static string GetTokenText(SyntaxToken caretToken)
        {
            bool isMethod = caretToken.IsKind(SyntaxKind.MethodDeclaration)
                                || caretToken.Parent?.IsKind(SyntaxKind.MethodDeclaration) == true;
            string methodCall = isMethod ? "()" : "";
            string tokenText = $"{caretToken.Text}{methodCall}";

            return tokenText;
        }

        #region Append to Debugger Display

        /// <summary>
        /// TODO: Handle case where DebuggerDisplay() had empty parameters, which would cause
        /// a syntactically-incorrect plus sign to be added.
        /// </summary>
        private static void AppendToDebuggerDisplay(ITextEdit edit, string allText, ClassDeclarationSyntax classDeclaration, SyntaxToken caretToken)
        {
            const string DebuggerDisplayInvoke = "DebuggerDisplay(";

            string leftPlusSign = "";
            string leftQuotationMark = "";
            string rightQuotationMark = "";
            string rightParenthesis = "";
            string leftSpace = " ";

            AttributeListSyntax debuggerDisplayList = classDeclaration.AttributeLists
                .Where(x => x.ToString().Contains(DebuggerDisplayInvoke))
                .First();

            // No quotation marks - track last parenthesis instead, and add quotation marks later
            if (!TryGetLastIndexOf(allText, debuggerDisplayList, '"', out int lastQuotationMark))
            {
                leftQuotationMark = "\"";
                rightQuotationMark = "\"";

                if (!TryGetLastIndexOf(allText, debuggerDisplayList, ')', out lastQuotationMark))
                {
                    rightParenthesis = ")";
                    lastQuotationMark = debuggerDisplayList.Span.End - 1;
                }

                if (!DebuggerDisplayIsParameterless(debuggerDisplayList))
                    leftPlusSign = " + ";
                else
                    leftSpace = "";
            }
            else if (allText[lastQuotationMark - 1] == '"')
                leftSpace = "";

            string tokenText = GetTokenText(caretToken);
            string insert = $"{leftSpace}{leftPlusSign}{leftQuotationMark}{{{tokenText}}}{rightQuotationMark}{rightParenthesis}";
            edit.Insert(lastQuotationMark, insert);
        }

        private static bool TryGetLastIndexOf(string allText, CSharpSyntaxNode node, char c, out int lastIndex)
        {
            lastIndex = allText.LastIndexOf(c, node.Span.End);

            if (lastIndex == -1 || lastIndex < node.SpanStart)
                return false;
            return true;
        }

        private static bool DebuggerDisplayIsParameterless(AttributeListSyntax debuggerDisplayList)
        {
            const string DebuggerDisplayText = "DebuggerDisplay";

            AttributeSyntax debuggerDisplay = debuggerDisplayList.Attributes.Where(x => x.Name.ToString().Contains(DebuggerDisplayText)).First();
            bool isParameterless = !debuggerDisplay.ArgumentList.Arguments.Any();
            return isParameterless;
        }

        #endregion

        #endregion

        private static void AddDiagnosticsToUsings(ITextEdit edit, CompilationUnitSyntax root)
        {
            var usings = root.Usings;

            if (!usings.Where(x => x.Name.ToString().Contains("System.Diagnostics")).Any())
            {
                int usingDiagnosticsStart = usings.Span.End;
                string usingDiagnostics = $"{Environment.NewLine}using System.Diagnostics;";
                edit.Insert(usingDiagnosticsStart, usingDiagnostics);
            }
        }

        #region Set Caret
        private static async Task SetCaretToDebuggerDisplayAsync(ITextBuffer textBuffer, ITrackingPoint classTrackingPoint, DTE dte)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var snapshot = textBuffer.CurrentSnapshot;
            string allText = snapshot.GetText();
            int trackedPosition = classTrackingPoint.GetPosition(snapshot);

            int newCursorPosition = GetNewCursorPosition(allText, trackedPosition);
            int lineNumber = GetLineNumber(allText, newCursorPosition, out int offsetInLine);

            var selection = (TextSelection)dte.ActiveDocument.Selection;
            selection.MoveTo(lineNumber, offsetInLine + 1);
        }

        /// <summary>
        /// Currently returns the index of the last quotation mark in the DebuggerDisplay.
        /// </summary>
        private static int GetNewCursorPosition(string allText, int trackedPosition)
        {
            const string DebuggerDisplayTarget = "DebuggerDisplay(";
            int debuggerDisplayIndex = allText.LastIndexOf(DebuggerDisplayTarget, trackedPosition + DebuggerDisplayTarget.Length);
            int newLineIndex = allText.IndexOfAny(new char[] { '\r', '\n' }, debuggerDisplayIndex);
            int lastQuoteIndex = allText.LastIndexOf('"', newLineIndex);

            return lastQuoteIndex;
        }

        private static int GetLineNumber(string allText, int position, out int offsetInLine)
        {
            int lineNumber = 1;
            offsetInLine = 0;
            for (int i = 0; i < position; i++)
            {
                if (allText[i] == '\n')
                {
                    lineNumber++;
                    offsetInLine = 0;
                }
                else
                    offsetInLine++;
            }
            return lineNumber;
        }

        #endregion
    }
}
