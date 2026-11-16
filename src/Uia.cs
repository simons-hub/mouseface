using System;
using System.Runtime.InteropServices;

namespace MouseFace
{
    // UI Automation (UIAutomationCore.dll, part of Windows), declared by hand because the compiler
    // that ships with Windows has no import for it. .NET's own System.Windows.Automation could not
    // find some terminals' text from a screen point, so this is the COM interface screen readers use.
    // Methods this program never calls are placeholders, there only to keep each called method at its
    // place in the interface's table; their order follows UIAutomationClient.idl and must not change.
    [StructLayout(LayoutKind.Sequential)] internal struct UiaPoint { public int x, y; }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] internal class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void _CompareElements(); void _CompareRuntimeIds();
        IUIAutomationElement GetRootElement();
        void _ElementFromHandle();
        IUIAutomationElement ElementFromPoint(UiaPoint pt);
        IUIAutomationElement GetFocusedElement();
        void _GetRootElementBuildCache(); void _ElementFromHandleBuildCache(); void _ElementFromPointBuildCache();
        void _GetFocusedElementBuildCache(); void _CreateTreeWalker(); void _ControlViewWalker(); void _ContentViewWalker();
        IUIAutomationTreeWalker RawViewWalker { get; }
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void _SetFocus(); void _GetRuntimeId(); void _FindFirst(); void _FindAll(); void _FindFirstBuildCache();
        void _FindAllBuildCache(); void _BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        void _GetCurrentPropertyValueEx();
        void _GetCachedPropertyValue(); void _GetCachedPropertyValueEx(); void _GetCurrentPatternAs(); void _GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
        void _GetCachedPattern(); void _GetCachedParent(); void _GetCachedChildren();
        int CurrentProcessId { get; }
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTreeWalker
    {
        IUIAutomationElement GetParentElement(IUIAutomationElement element);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextPattern
    {
        void _RangeFromPoint(); void _RangeFromChild();
        IUIAutomationTextRangeArray GetSelection();
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRangeArray
    {
        int Length { get; }
        IUIAutomationTextRange GetElement(int index);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRange
    {
        IUIAutomationTextRange Clone();
        int Compare(IUIAutomationTextRange range); // nonzero when both cover the same text
        void _CompareEndpoints(); void _ExpandToEnclosingUnit(); void _FindAttribute(); void _FindText();
        void _GetAttributeValue();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] double[] GetBoundingRectangles();
        void _GetEnclosingElement();
        [return: MarshalAs(UnmanagedType.BStr)] string GetText(int maxLength);
        void _Move();
        int MoveEndpointByUnit(int endpoint, int unit, int count);
        void MoveEndpointByRange(int srcEndPoint, IUIAutomationTextRange range, int targetEndPoint);
        void Select();
    }
}
