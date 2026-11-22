// Narrow managed host doubles: no Capture One process, documents, photos or native UI.
using System;
using System.Collections.Generic;
using System.Windows;
using P1.C1.Core.Interfaces;
using P1.C1.ViewModels.Commands;

internal static class StartupHook
{
    internal const string ProductName = "Spektrafilm test";
    internal static void Log(string text) { }
}
internal static class ApprovedCatalog { internal static bool IsApproved(string? path) => path == "test-copy"; }
namespace System.Windows.Forms
{
    public class Form : IDisposable { public bool IsDisposed { get; private set; } public void Dispose() => IsDisposed = true; }
    public enum MessageBoxButtons { OK }
    public enum MessageBoxIcon { Warning }
    public static class MessageBox
    {
        public static readonly List<string> Warnings = new();
        public static void Show(Form owner, string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon) => Warnings.Add(message);
    }
}
namespace P1.C1.Core.ModelEntities { }
namespace P1.C1.Core.Interfaces
{
    public interface ICaptureOneDocument { }
    public interface ISelectedCollectionsProvider { Selection? PrimarySelectedCollectionSelection { get; } }
    public sealed class Selection { public object? PrimaryVariant { get; set; } public List<object>? SelectedVariants { get; set; } }
    public sealed class Destination { public string Name { get; init; } = "One recipe"; }
    public interface IDestinationDocumentManager { List<Destination> EnabledDestinations { get; } Destination? SelectedDestination { get; } }
}
namespace P1.C1.ViewModels.Commands
{
    public class Command { }
    public delegate void EditMultipleCommandEventHandler(Command command, bool editMultiple);
    public sealed class EditMultipleCommand : Command
    {
        public string Name { get; init; } = "Export";
        public EditMultipleCommandEventHandler? OnExecuteMultiple;
        public void Execute(bool editMultiple) => OnExecuteMultiple?.Invoke(this, editMultiple);
    }
    public sealed class Commands
    {
        public EditMultipleCommand Export { get; } = new();
        public EditMultipleCommand ExportWithPreviousSettings { get; } = new();
    }
}
namespace P1.C1.Components.CommandsLogic
{
    public sealed class CommandHookerFile
    {
        public readonly object m_mainForm;
        public readonly List<bool> NativeDialog = new(), NativePrevious = new();
        public CommandHookerFile(object form) => m_mainForm = form;
        public void OnExportOnExecuteMultiple(Command command, bool editMultiple) => NativeDialog.Add(editMultiple);
        public void OnExportWithPreviousOnExecuteMultiple(Command command, bool editMultiple) => NativePrevious.Add(editMultiple);
    }
}
namespace P1.C1.Core.Messaging { public static class MessageBusProvider { public static object DefaultInstance { get; } = new(); } }
namespace P1.C1.Components
{
    public sealed class TestForm : System.Windows.Forms.Form { public ICaptureOneDocument Document { get; init; } = null!; }
}
namespace P1.C1.Components.ExportDialog
{
    public sealed class ExportDialog : Window { }
    public class ActionCommandBase
    {
        private readonly Delegate m_actionToExecute;
        public ActionCommandBase(Delegate action) => m_actionToExecute = action;
    }
    public sealed class ActionCommand : ActionCommandBase, System.Windows.Input.ICommand
    {
        public ActionCommand(Delegate action) : base(action) { }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
    public sealed class ConfirmationClosure
    {
        public Func<Action, System.Threading.Tasks.Task<bool>> argExportAction = _ => System.Threading.Tasks.Task.FromResult(true);
        public void Invoke() { }
    }
    public sealed class DialogModel { public System.Windows.Input.ICommand ExportCommand { get; } = new ActionCommand(new Action(new ConfirmationClosure().Invoke)); }
    public sealed class ExportDialogFactory
    {
        private readonly ExportDialog m_exportDialogWindow = new();
        public DialogModel DialogVM { get; } = new();
        private readonly bool editMultiple;
        public static readonly List<bool> Opened = new();
        public ExportDialogFactory(ICaptureOneDocument doc, object bus, bool multiple) => editMultiple = multiple;
        public void Show(System.Windows.Forms.Form owner) => Opened.Add(editMultiple);
    }
}
namespace P1.C1
{
    public static class Program { public static TestManager DocumentManager { get; set; } = null!; }
    public sealed class TestDocument : ICaptureOneDocument, ISelectedCollectionsProvider
    {
        public Selection? PrimarySelectedCollectionSelection { get; set; }
    }
    public sealed class TestManager : IDestinationDocumentManager
    {
        public ICaptureOneDocument ActiveDocument { get; set; } = null!;
        public string ActiveDocumentPath { get; set; } = "test-copy";
        public Commands Commands { get; } = new();
        public List<Destination> EnabledDestinations { get; } = new() { new() };
        public Destination? SelectedDestination => EnabledDestinations.Count > 0 ? EnabledDestinations[0] : null;
        public Commands GetDocumentCommands(ICaptureOneDocument document) => Commands;
        public IDestinationDocumentManager GetDestinationDocumentManager(ICaptureOneDocument document) => this;
    }
}
