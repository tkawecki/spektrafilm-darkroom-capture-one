using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using P1.C1.Components;
using P1.C1.Components.CommandsLogic;
using P1.C1.Components.ExportDialog;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.C1.Core.Messaging;
using P1.C1.ViewModels.Commands;
using Forms = System.Windows.Forms;

internal sealed class ExportHook : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Forms.Form window;
    private readonly ICaptureOneDocument document;
    private readonly object manager;
    private readonly PropertyInfo activeDocument;
    private readonly PropertyInfo activePath;

    private readonly Func<IDestinationDocumentManager> destinations;
    private readonly Func<bool, bool> isActive;
    private readonly Func<bool, Task<bool>> export;
    // True while Spektrafilm exports with the save dialog, which needs exactly one recipe. Recipe export uses
    // every recipe Capture One would use, so the check is skipped then.
    private readonly Func<bool> singleRecipeRequired;
    private readonly CommandBinding normal;
    private readonly CommandBinding previous;
    private ExportDialog? dialog;
    private IDisposable? confirmation;
    private bool busy;
    private bool disposed;

    internal ExportHook(Forms.Form window, Func<bool, bool> isActive, Func<bool, Task<bool>> export,
        Func<bool>? singleRecipeRequired = null)
    {
        this.window = window;
        this.isActive = isActive;
        this.export = export;
        this.singleRecipeRequired = singleRecipeRequired ?? (() => true);
        var program = window.GetType().Assembly.GetType("P1.C1.Program", throwOnError: true)!;
        manager = program.GetProperty("DocumentManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Capture One’s document manager is unavailable.");
        activeDocument = FindProperty(manager, "ActiveDocument");
        activePath = FindProperty(manager, "ActiveDocumentPath");

        document = FindProperty(window, "Document").GetValue(window) as ICaptureOneDocument
            ?? throw new InvalidOperationException("The selected catalog is unavailable.");
        if (!IsApprovedDocument())
            throw new InvalidOperationException("Activate the selected catalog before binding Export.");
        var commands = manager.GetType().GetMethod("GetDocumentCommands", Instance, null,
                new[] { typeof(ICaptureOneDocument) }, null)?.Invoke(manager, new object[] { document }) as Commands
            ?? throw new InvalidOperationException("Capture One’s export commands are unavailable.");
        var getDestinationManager = manager.GetType().GetMethod("GetDestinationDocumentManager", Instance, null,
            new[] { typeof(ICaptureOneDocument) }, null)
            ?? throw new MissingMethodException(manager.GetType().FullName, "GetDestinationDocumentManager");
        var readDestinations = getDestinationManager.CreateDelegate<Func<ICaptureOneDocument, IDestinationDocumentManager>>(manager);
        destinations = () => readDestinations(document);
        normal = new CommandBinding(commands.Export, window, "OnExportOnExecuteMultiple", OpenDialog);
        try
        {
            previous = new CommandBinding(commands.ExportWithPreviousSettings, window,
                "OnExportWithPreviousOnExecuteMultiple", ExportWithPrevious);
        }
        catch
        {
            normal.Dispose();
            throw;
        }
        StartupHook.Log("Spektrafilm bound the selected catalog’s native Export commands.");
    }

    internal Window? Dialog => dialog;

    private bool IsApprovedDocument() =>
        !window.IsDisposed && ReferenceEquals(activeDocument.GetValue(manager), document)
        && ApprovedCatalog.IsApproved(activePath.GetValue(manager) as string);

    private bool ShouldIntercept(bool editMultiple) => !disposed && IsApprovedDocument() && isActive(editMultiple);

    private void OpenDialog(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept(editMultiple))
            {
                normal.Original(command, editMultiple);
                return;
            }
            if (busy || dialog is not null)
                return;
            if (!HasSelection(editMultiple))
            {
                Warn("Select at least one variant to export with Spektrafilm.");
                return;
            }

            var factory = PrepareDialog(editMultiple);
            try { factory.Show(window); }
            finally { ReleaseDialog(); }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task<bool> Confirm(Func<Task<bool>> native, bool editMultiple)
    {
        try
        {
            if (disposed || !IsApprovedDocument() || busy)
                return false;
            if (!isActive(editMultiple))
                return await native();
            if (!HasSelection(editMultiple))
            {
                Warn("Select at least one variant to export with Spektrafilm.");
                return false;
            }
            if (singleRecipeRequired() && !CheckRecipe())
                return false;
            await ExportAsync(editMultiple);
        }
        catch (Exception error)
        {
            Fail(error);
        }
        // The native dialog command closes itself only when its action returns true.
        return false;
    }

    private void ExportWithPrevious(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept(editMultiple))
            {
                previous.Original(command, editMultiple);
                return;
            }
            if (!HasSelection(editMultiple))
            {
                Warn("Select at least one variant to export with Spektrafilm.");
                return;
            }
            if (!busy && (!singleRecipeRequired() || CheckRecipe()))
                _ = ExportAsync(editMultiple);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private bool HasSelection(bool editMultiple) =>
        document is ISelectedCollectionsProvider { PrimarySelectedCollectionSelection: { PrimaryVariant: not null } selection }
        && (!editMultiple || selection.SelectedVariants?.Count > 0);

    private bool CheckRecipe()
    {
        var manager = destinations();
        var enabled = manager.EnabledDestinations;
        if (enabled.Count == 1 && StringComparer.Ordinal.Equals(enabled[0].Name, manager.SelectedDestination?.Name))
            return true;
        Warn("Enable and select one Export Recipe to use Spektrafilm.");
        return false;
    }

    private ExportDialogFactory PrepareDialog(bool editMultiple)
    {
        var factory = new ExportDialogFactory(document, MessageBusProvider.DefaultInstance, editMultiple);
        try
        {
            confirmation = ConfirmationBinding.Attach(factory.DialogVM.ExportCommand, native => Confirm(native, editMultiple));
            dialog = GetDialogWindow(factory);
            dialog.Title += " – " + StartupHook.ProductName;
            // 16.9 owns the modal in ExportDialogFactory, not MainForm.
            StartupHook.Log("Spektrafilm bound the native Export confirmation button.");
            return factory;
        }
        catch
        {
            ReleaseDialog();
            GetDialogWindow(factory).Close();
            throw;
        }
    }

    private static ExportDialog GetDialogWindow(ExportDialogFactory factory) =>
        typeof(ExportDialogFactory).GetField("m_exportDialogWindow", Instance)?.GetValue(factory) as ExportDialog
        ?? throw new MissingMemberException("Capture One 16.9 export dialog is unavailable.");

    private void ReleaseDialog()
    {
        confirmation?.Dispose();
        confirmation = null;
        dialog = null;

    }

    private async Task<bool> ExportAsync(bool editMultiple)
    {
        busy = true;
        var owner = dialog;
        try
        {
            StartupHook.Log("Native Export routed to the Spektrafilm renderer.");
            if (!await export(editMultiple))
                return false;
            if (!disposed && owner is { IsVisible: true } && ReferenceEquals(owner, dialog))
                _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!disposed && owner.IsVisible && ReferenceEquals(owner, dialog))
                        owner.Close();
                }));
            return true;
        }
        catch (Exception error)
        {
            Fail(error);
            return false;
        }
        finally
        {
            busy = false;
        }
    }

    private void Fail(Exception error)
    {
        StartupHook.Log(error.ToString());
        Warn($"Spektrafilm export could not start. {error.GetBaseException().Message}");
    }

    private void Warn(string message)
    {
        if (dialog is { IsVisible: true } owner)
            System.Windows.MessageBox.Show(owner, message, StartupHook.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!window.IsDisposed)
            Forms.MessageBox.Show(window, message, StartupHook.ProductName, Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            if (dialog is { } owner)
                owner.Close();
            ReleaseDialog();
        }
        finally
        {
            previous.Dispose();
            normal.Dispose();
        }
    }

    private static PropertyInfo FindProperty(object target, string name) =>
        target.GetType().GetProperty(name, Instance) ?? throw new MissingMemberException(target.GetType().FullName, name);

    private sealed class CommandBinding : IDisposable
    {
        private static readonly FieldInfo Callbacks = typeof(EditMultipleCommand).GetField("OnExecuteMultiple", Instance)
            ?? throw new MissingFieldException(typeof(EditMultipleCommand).FullName, "OnExecuteMultiple");
        private readonly EditMultipleCommand command;
        private readonly EditMultipleCommandEventHandler wrapper;

        internal EditMultipleCommandEventHandler Original { get; }

        internal CommandBinding(EditMultipleCommand command, object window, string method,
            EditMultipleCommandEventHandler wrapper)
        {
            this.command = command;
            this.wrapper = wrapper;
            var callbacks = (EditMultipleCommandEventHandler?)Callbacks.GetValue(command)
                ?? throw new InvalidOperationException($"Capture One did not register {command.Name}.");
            Original = callbacks.GetInvocationList().OfType<EditMultipleCommandEventHandler>()
                .Single(callback => callback.Method.Name == method
                    && callback.Target?.GetType().FullName == "P1.C1.Components.CommandsLogic.CommandHookerFile"
                    && ReferenceEquals(callback.Target.GetType().GetField("m_mainForm", Instance)?.GetValue(callback.Target), window));
            Replace(callbacks, Original, wrapper);
        }

        public void Dispose()
        {
            if (Callbacks.GetValue(command) is EditMultipleCommandEventHandler current)
                Replace(current, wrapper, Original);
        }

        private void Replace(EditMultipleCommandEventHandler callbacks, EditMultipleCommandEventHandler before,
            EditMultipleCommandEventHandler after)
        {
            var invocation = callbacks.GetInvocationList();
            var index = Array.IndexOf(invocation, before);
            if (index < 0)
                return;
            invocation[index] = after;
            Callbacks.SetValue(command, Delegate.Combine(invocation));
        }
    }

    private sealed class ConfirmationBinding : IDisposable
    {
        private readonly object closure;
        private readonly FieldInfo field;
        private readonly Func<Action, Task<bool>> original;
        private readonly Func<Action, Task<bool>> wrapper;

        private ConfirmationBinding(object closure, FieldInfo field, Func<Action, Task<bool>> original, Func<Func<Task<bool>>, Task<bool>> confirm)
        {
            this.closure = closure;
            this.field = field;
            this.original = original;
            wrapper = enable => confirm(() => original(enable));
            field.SetValue(closure, wrapper);
        }

        internal static ConfirmationBinding Attach(System.Windows.Input.ICommand command, Func<Func<Task<bool>>, Task<bool>> confirm)
        {
            var actionField = command.GetType().BaseType?.GetField("m_actionToExecute", Instance)
                ?? throw new MissingFieldException(command.GetType().FullName, "m_actionToExecute");
            if (actionField.GetValue(command) is not Delegate { Target: { } closure })
                throw new InvalidOperationException("Capture One did not expose its Export confirmation action.");
            var exportAction = closure.GetType().GetField("argExportAction", Instance)
                ?? throw new MissingFieldException(closure.GetType().FullName, "argExportAction");
            if (exportAction.GetValue(closure) is not Func<Action, Task<bool>> native)
                throw new InvalidOperationException("Capture One’s Export confirmation action has an unexpected type.");
            return new ConfirmationBinding(closure, exportAction, native, confirm);
        }

        public void Dispose()
        {
            if (ReferenceEquals(field.GetValue(closure), wrapper))
                field.SetValue(closure, original);
        }
    }
}
