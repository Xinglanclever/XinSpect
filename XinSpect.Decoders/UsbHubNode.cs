namespace XinSpect;

/// <summary>
/// <c>IOCTL_USB_GET_NODE_INFORMATION</c> 回傳的 <c>USB_NODE_INFORMATION</c> 中與埠數有關的欄位。
/// </summary>
/// <param name="NodeType">0＝<c>UsbHub</c>（1＝<c>UsbMiParent</c>）。</param>
/// <param name="DescriptorLength">hub descriptor 的 <c>bLength</c>（USB 2.0 版是 9）。</param>
/// <param name="DescriptorType">hub descriptor 的 <c>bDescriptorType</c>，應為 <c>0x29</c>。</param>
/// <param name="NumberOfPorts">hub descriptor 的 <c>bNumberOfPorts</c>（u16）。</param>
public sealed record UsbHubNodeInfo(
    int NodeType,
    int DescriptorLength,
    byte DescriptorType,
    ushort NumberOfPorts)
{
    /// <summary>這是 hub descriptor（<c>0x29</c>）且長度至少 9——不是的話埠數不能當真。</summary>
    public bool IsHubDescriptor => DescriptorType == 0x29 && DescriptorLength >= 9;
}

/// <summary>
/// USB 根集線器 <c>IOCTL_USB_GET_NODE_INFORMATION</c> 的解碼器（U10 第三圈 NP-0003）。
/// 純位元組解析，不開裝置。
/// </summary>
/// <remarks>
/// <b>為什麼要這把鍵：</b>既有的 USB 事實只有三把（控制器數、裝置數、最忙的控制器），
/// 那三把回答「誰掛得最多」，回答不了使用者最常問的那一句「**還能不能再接**」。
/// 埠數只有硬體自己知道，而它就在 hub descriptor 裡。
/// </remarks>
public static class UsbHubNodeDecoder
{
    /// <summary>解析 <c>USB_NODE_INFORMATION</c>。不足 12 位元組或不是 hub 節點回 <c>null</c>。</summary>
    [SpecRef("Windows SDK usbioctl.h：IOCTL_USB_GET_NODE_INFORMATION 回傳 USB_NODE_INFORMATION，"
        + "NodeType（USB_NODE_TYPE，u32@0；0＝UsbHub）之後接 union，USB_HUB_INFORMATION 自位移 4 起；"
        + "其 USB_HUB_DESCRIPTOR 在位移 4：bLength u8／bDescriptorType u8（0x29）／bNumberOfPorts u16。"
        + "界線＝只解到埠數所需的欄位，hub characteristics 與電源欄位本版不解讀（本機三顆根的 0x0200 "
        + "含 USB 2.0 定義以外的位元，讀不出規則就不編規則）")]
    public static UsbHubNodeInfo? ParseNodeInformation(byte[] out_)
    {
        // NodeType(4) + bLength(1) + bDescriptorType(1) + bNumberOfPorts(2) = 8，再多留一點餘裕。
        if (out_.Length < 12) return null;
        int nodeType = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(out_.AsSpan(0, 4));
        int descriptorLength = out_[4];
        byte descriptorType = out_[5];
        ushort ports = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(out_.AsSpan(6, 2));
        if (nodeType != 0) return null; // 不是 UsbHub（例如 UsbMiParent）
        return new UsbHubNodeInfo(nodeType, descriptorLength, descriptorType, ports);
    }
}
