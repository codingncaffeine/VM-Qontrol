#!/usr/bin/env python3
"""A stand-in tray host on the ISOLATED test bus: owns org.kde.StatusNotifierWatcher, records the item the app
registers, then reads it back the way a panel does (its title/tooltip and its dbusmenu layout). Writes report.txt."""
import sys, dbus, dbus.service, dbus.mainloop.glib
from gi.repository import GLib

dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
out = open(sys.argv[1], "w")
loop = GLib.MainLoop()
IFACE = "org.kde.StatusNotifierWatcher"

class Watcher(dbus.service.Object):
    def __init__(self):
        self.items = []
        super().__init__(dbus.service.BusName(IFACE, bus), "/StatusNotifierWatcher")

    @dbus.service.method(IFACE, in_signature="s", sender_keyword="sender")
    def RegisterStatusNotifierItem(self, service, sender=None):
        path = service if service.startswith("/") else "/StatusNotifierItem"
        name = sender if service.startswith("/") else service
        out.write(f"registered: name={name} path={path}\n"); out.flush()
        self.items.append(f"{name}{path}")
        GLib.timeout_add(1500, lambda: read(name, path))

    @dbus.service.method(IFACE, in_signature="s")
    def RegisterStatusNotifierHost(self, service):
        pass

    @dbus.service.method(dbus.PROPERTIES_IFACE, in_signature="ss", out_signature="v")
    def Get(self, iface, prop):
        return self.GetAll(iface)[prop]

    @dbus.service.method(dbus.PROPERTIES_IFACE, in_signature="s", out_signature="a{sv}")
    def GetAll(self, iface):
        return {"IsStatusNotifierHostRegistered": True, "ProtocolVersion": dbus.Int32(0),
                "RegisteredStatusNotifierItems": dbus.Array(self.items, signature="s")}

def labels(layout, depth=0):
    _id, props, children = layout
    lines = []
    if props.get("type") == "separator":
        lines.append("  " * depth + "---")
    elif "label" in props:
        lines.append("  " * depth + str(props["label"]) + ("" if props.get("enabled", True) else "  [disabled]"))
    for c in children:
        lines += labels(c, depth + (1 if "label" in props else 0))
    return lines

def read(name, path):
    try:
        item = bus.get_object(name, path)
        props = dbus.Interface(item, dbus.PROPERTIES_IFACE).GetAll("org.kde.StatusNotifierItem")
        out.write(f"title: {props.get('Title')}\n")
        tip = props.get("ToolTip")
        out.write(f"tooltip: {tip[2] if tip else None}\n")
        menu_path = props.get("Menu")
        out.write(f"menu path: {menu_path}\n")
        menu = bus.get_object(name, menu_path)
        rev, layout = dbus.Interface(menu, "com.canonical.dbusmenu").GetLayout(0, -1, dbus.Array([], signature="s"))
        out.write("menu:\n" + "\n".join(labels(layout)) + "\n")
        # Click "Quit VM Qontrol" the way a panel does: a dbusmenu "clicked" event on its id.
        def find(node, text):
            _id, props, children = node
            if str(props.get("label", "")) == text: return _id
            for c in children:
                f = find(c, text)
                if f is not None: return f
        quit_id = find(layout, "Quit VM Qontrol")
        dbus.Interface(menu, "com.canonical.dbusmenu").Event(quit_id, "clicked", dbus.Int32(0, variant_level=1), dbus.UInt32(0))
        out.write(f"clicked Quit (id {quit_id})\n")
    except Exception as e:  # report, never hang
        out.write(f"read failed: {e}\n")
    out.flush()
    loop.quit()
    return False

Watcher()
GLib.timeout_add(25000, loop.quit)
loop.run()
