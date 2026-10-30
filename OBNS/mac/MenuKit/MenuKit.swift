// OBNS: not an upstream file. The main menu's native controls on macOS (OBNS/GLFORMS.md, "Native"): AppKit windows and
// controls made and changed through a small C interface, and their events given back through one callback. The menu's
// logic and layout stay upstream's formMain, in C#, on OBNS's control classes: the host there (Host.Native.cs) mirrors its
// control tree here, so every control sits where the Windows menu has it; only how it looks is the Mac's.
// Built on a Mac by OBNS/mac/MenuKit/build.sh into libOBNSMenuKit.dylib, which sits beside OpenBve.exe.

import AppKit

public typealias MKEventCallback = @convention(c) (Int32, Int32, Int32, Int32, UnsafePointer<CChar>?) -> Void

/// The kinds of control (Host.Native.cs has the same numbers).
private enum Kind: Int32 {
	case container = 0, label, link, button, check, radio, group, field, area, popup, combo, stepper, slider, table, tabs, image, scroll, toggle
}

/// The events given back (Host.Native.cs has the same numbers).
private enum Event: Int32 {
	case click = 1, doubleClick, text, select, close, resize, column, link, drop, mouseDown, mouseUp, mouseMove, value, activate, key
}

private var callback: MKEventCallback?
private var nextId: Int32 = 1
private var views: [Int32: NSView] = [:]
private var windows: [Int32: MKWindow] = [:]
private var images: [Int32: NSImage] = [:]

private func emit(_ id: Int32, _ event: Event, _ a: Int32 = 0, _ b: Int32 = 0, _ text: String? = nil) {
	guard let callback = callback else { return }
	if let text = text {
		text.withCString { callback(id, event.rawValue, a, b, $0) }
	} else {
		callback(id, event.rawValue, a, b, nil)
	}
}

private func newId() -> Int32 {
	let id = nextId
	nextId += 1
	return id
}

private func string(_ text: UnsafePointer<CChar>?) -> String {
	return text == nil ? "" : String(cString: text!)
}

private func strings(_ list: UnsafePointer<UnsafePointer<CChar>?>?, _ count: Int32) -> [String] {
	guard let list = list else { return [] }
	return (0..<Int(count)).map { string(list[$0]) }
}

/// A view whose origin is its top left, as the forms' coordinates are.
private class FlippedView: NSView {
	override var isFlipped: Bool { return true }
	var id: Int32 = 0
	/// The control's own background (a form's colour where it has one).
	var fill: NSColor? {
		didSet { needsDisplay = true }
	}

	override func draw(_ dirtyRect: NSRect) {
		if let fill = fill {
			fill.setFill()
			dirtyRect.fill()
		}
	}
}

/// A picture of a control that paints itself, or a picture box: the mouse goes to the control.
private class MKImageView: NSImageView {
	override var isFlipped: Bool { return true }
	var id: Int32 = 0

	private func place(_ event: NSEvent) -> NSPoint {
		return convert(event.locationInWindow, from: nil)
	}

	override func mouseDown(with event: NSEvent) {
		let p = place(event)
		emit(id, event.clickCount == 2 ? .doubleClick : .mouseDown, Int32(p.x), Int32(p.y))
	}

	override func mouseUp(with event: NSEvent) {
		let p = place(event)
		emit(id, .mouseUp, Int32(p.x), Int32(p.y))
	}

	override func mouseDragged(with event: NSEvent) {
		let p = place(event)
		emit(id, .mouseMove, Int32(p.x), Int32(p.y))
	}
}

/// A window: its close box and size go to the form; files can be dropped on it (package installation).
private class MKWindow: NSWindow, NSWindowDelegate {
	var id: Int32 = 0
	var root: FlippedView!
	var parentSheet: NSWindow?

	func windowShouldClose(_ sender: NSWindow) -> Bool {
		emit(id, .close)
		return false
	}

	func windowDidResize(_ notification: Notification) {
		let size = contentLayoutRect.size
		emit(id, .resize, Int32(size.width), Int32(size.height))
	}
}

private class DropView: FlippedView {
	override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
		return .copy
	}

	override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
		guard let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL] else { return false }
		for url in urls {
			emit(id, .drop, 0, 0, url.path)
		}

		return true
	}
}

/// The controls' actions and delegates; a control's tag is its id.
private class Target: NSObject, NSTextFieldDelegate, NSTextViewDelegate, NSComboBoxDelegate {
	static let shared = Target()

	@objc func clicked(_ sender: NSControl) {
		emit(Int32(sender.tag), .click)
	}

	@objc func selected(_ sender: NSPopUpButton) {
		emit(Int32(sender.tag), .select, Int32(sender.indexOfSelectedItem))
	}

	@objc func segment(_ sender: NSSegmentedControl) {
		emit(Int32(sender.tag), .select, Int32(sender.selectedSegment))
	}

	@objc func slid(_ sender: NSSlider) {
		emit(Int32(sender.tag), .value, Int32(sender.integerValue))
	}

	@objc func stepped(_ sender: NSStepper) {
		if let stepper = sender.superview as? MKStepper {
			stepper.field.stringValue = stepper.format(sender.doubleValue)
			emit(stepper.id, .text, 0, 0, stepper.field.stringValue)
		}
	}

	func controlTextDidChange(_ notification: Notification) {
		guard let field = notification.object as? NSTextField else { return }
		if let stepper = field.superview as? MKStepper {
			emit(stepper.id, .text, 0, 0, field.stringValue)
		} else {
			emit(Int32(field.tag), .text, 0, 0, field.stringValue)
		}
	}

	func comboBoxSelectionDidChange(_ notification: Notification) {
		if let combo = notification.object as? NSComboBox {
			emit(Int32(combo.tag), .select, Int32(combo.indexOfSelectedItem))
		}
	}

	func textDidChange(_ notification: Notification) {
		if let text = notification.object as? MKTextView {
			emit(text.id, .text, 0, 0, text.string)
		}
	}

	func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
		if let text = textView as? MKTextView {
			emit(text.id, .link, 0, 0, (link as? URL)?.absoluteString ?? (link as? String) ?? "")
			return true
		}

		return false
	}
}

private class MKTextView: NSTextView {
	var id: Int32 = 0
}

/// A number with its arrows (NumericUpDown).
private class MKStepper: FlippedView {
	let field = NSTextField()
	let stepper = NSStepper()
	var decimals: Int32 = 0

	func format(_ value: Double) -> String {
		return String(format: "%.\(decimals)f", value)
	}

	override func layout() {
		super.layout()
		let arrows: CGFloat = 19
		field.frame = NSRect(x: 0, y: 0, width: max(0, bounds.width - arrows - 2), height: bounds.height)
		stepper.frame = NSRect(x: bounds.width - arrows, y: 0, width: arrows, height: bounds.height)
	}
}

/// A list (ListView, DataGridView): rows of strings with an icon each.
private class MKTable: NSScrollView, NSTableViewDataSource, NSTableViewDelegate {
	var id: Int32 = 0
	let table = NSTableView()
	var rows: [[String]] = []
	var icons: [Int32] = []
	var quiet = false

	func setUp() {
		documentView = table
		hasVerticalScroller = true
		hasHorizontalScroller = true
		autohidesScrollers = true
		borderType = .bezelBorder
		table.dataSource = self
		table.delegate = self
		table.usesAlternatingRowBackgroundColors = true
		table.target = self
		table.doubleAction = #selector(activated)
		table.rowHeight = 17
		table.style = .fullWidth
	}

	@objc func activated() {
		emit(id, .activate, Int32(table.clickedRow))
	}

	func numberOfRows(in tableView: NSTableView) -> Int {
		return rows.count
	}

	func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
		guard let tableColumn = tableColumn, let column = tableView.tableColumns.firstIndex(of: tableColumn) else { return nil }
		let cell = NSTableCellView()
		let text = NSTextField(labelWithString: column < rows[row].count ? rows[row][column] : "")
		text.font = NSFont.systemFont(ofSize: 11)
		text.lineBreakMode = .byTruncatingTail
		text.translatesAutoresizingMaskIntoConstraints = false
		cell.addSubview(text)
		var leading: CGFloat = 2
		if column == 0, row < icons.count, let image = images[icons[row]] {
			let icon = NSImageView(image: image)
			icon.imageScaling = .scaleProportionallyDown
			icon.frame = NSRect(x: 2, y: 0, width: 16, height: 16)
			cell.addSubview(icon)
			leading = 20
		}

		NSLayoutConstraint.activate([
			text.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: leading),
			text.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -2),
			text.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
		])
		return cell
	}

	func tableViewSelectionDidChange(_ notification: Notification) {
		if !quiet {
			emit(id, .select, 0, 0, table.selectedRowIndexes.map { String($0) }.joined(separator: ","))
		}
	}

	func tableView(_ tableView: NSTableView, didClick tableColumn: NSTableColumn) {
		if let column = tableView.tableColumns.firstIndex(of: tableColumn) {
			emit(id, .column, Int32(column))
		}
	}
}

/// A tab control: its tabs at the top of its frame, its pages placed by the form below them.
private class MKTabs: FlippedView {
	let strip = NSSegmentedControl()
	let frameBox = NSBox()

	func setUp() {
		frameBox.boxType = .custom
		frameBox.borderColor = NSColor.separatorColor
		frameBox.fillColor = NSColor.clear
		frameBox.titlePosition = .noTitle
		addSubview(frameBox)
		strip.segmentStyle = .automatic
		strip.controlSize = .small
		strip.trackingMode = .selectOne
		strip.target = Target.shared
		strip.action = #selector(Target.segment(_:))
		addSubview(strip)
	}

	override func layout() {
		super.layout()
		strip.sizeToFit()
		strip.frame = NSRect(x: max(0, (bounds.width - strip.frame.width) / 2), y: 0, width: min(bounds.width, strip.frame.width), height: 21)
		frameBox.frame = NSRect(x: 0, y: 10, width: bounds.width, height: max(0, bounds.height - 10))
	}
}

/// A group box: a titled frame behind its children, which the form places itself.
private class MKGroup: FlippedView {
	let box = NSBox()

	override func layout() {
		super.layout()
		box.frame = bounds
	}
}

/// A panel that scrolls: its children are in the scroll view's document.
private class MKScroll: NSScrollView {
	var id: Int32 = 0
	let content = FlippedView()
}

// --- the application ---

@_cdecl("mk_init")
public func mk_init(_ events: MKEventCallback) {
	callback = events
	let app = NSApplication.shared
	app.setActivationPolicy(.regular)
	// Upstream's forms give some controls light colours of their own: always the light appearance, so they read.
	app.appearance = NSAppearance(named: .aqua)
	if app.mainMenu == nil {
		// The application menu, and the Edit menu text fields need for the Command keys.
		let main = NSMenu()
		let appItem = NSMenuItem()
		main.addItem(appItem)
		let appMenu = NSMenu()
		appMenu.addItem(withTitle: "Hide", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
		appMenu.addItem(NSMenuItem.separator())
		appMenu.addItem(withTitle: "Quit", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "q")
		appItem.submenu = appMenu
		let editItem = NSMenuItem()
		main.addItem(editItem)
		let edit = NSMenu(title: "Edit")
		edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
		edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
		edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
		edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
		editItem.submenu = edit
		app.mainMenu = main
	}

	app.finishLaunching()
	app.activate(ignoringOtherApps: true)
}

/// The menu is done: the game's window library makes its own application menu.
@_cdecl("mk_shutdown")
public func mk_shutdown() {
	NSApplication.shared.mainMenu = nil
	callback = nil
}

/// Handles the events waiting, after waiting up to so many seconds for the first.
@_cdecl("mk_pump")
public func mk_pump(_ wait: Double) {
	autoreleasepool {
		let app = NSApplication.shared
		var until = wait > 0 ? Date(timeIntervalSinceNow: wait) : Date.distantPast
		while let event = app.nextEvent(matching: .any, until: until, inMode: .default, dequeue: true) {
			if event.type == .keyDown, let responder = app.keyWindow?.firstResponder {
				var view = responder as? NSView
				if let text = responder as? MKTextView {
					view = text
				}

				var id: Int32 = 0
				while let current = view, id == 0 {
					if let field = current as? NSControl, field.tag != 0 {
						id = Int32(field.tag)
					} else if let text = current as? MKTextView {
						id = text.id
					} else if let table = current as? MKTable {
						id = table.id
					}

					view = current.superview
				}

				if id != 0 {
					emit(id, .key, Int32(event.keyCode), Int32(event.modifierFlags.rawValue >> 16))
				}
			}

			app.sendEvent(event)
			app.updateWindows()
			until = Date.distantPast
		}
	}
}

// --- windows ---

@_cdecl("mk_window_create")
public func mk_window_create(_ title: UnsafePointer<CChar>?, _ width: Int32, _ height: Int32, _ resizable: Int32) -> Int32 {
	var style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable]
	if resizable != 0 {
		style.insert(.resizable)
	}

	let window = MKWindow(contentRect: NSRect(x: 0, y: 0, width: Int(width), height: Int(height)), styleMask: style, backing: .buffered, defer: false)
	window.isReleasedWhenClosed = false
	window.title = string(title)
	window.delegate = window
	let id = newId()
	window.id = id
	let root = DropView(frame: NSRect(x: 0, y: 0, width: Int(width), height: Int(height)))
	root.id = id
	root.autoresizingMask = [.width, .height]
	root.registerForDraggedTypes([.fileURL])
	window.contentView = root
	window.root = root
	windows[id] = window
	views[id] = root
	window.center()
	return id
}

@_cdecl("mk_window_show")
public func mk_window_show(_ id: Int32, _ parent: Int32) {
	guard let window = windows[id] else { return }
	if parent != 0, let owner = windows[parent] {
		window.parentSheet = owner
		owner.beginSheet(window, completionHandler: nil)
	} else {
		window.makeKeyAndOrderFront(nil)
	}
}

@_cdecl("mk_window_destroy")
public func mk_window_destroy(_ id: Int32) {
	guard let window = windows[id] else { return }
	if let owner = window.parentSheet {
		owner.endSheet(window)
	}

	window.delegate = nil
	window.orderOut(nil)
	window.close()
	windows[id] = nil
	views[id] = nil
}

@_cdecl("mk_window_title")
public func mk_window_title(_ id: Int32, _ title: UnsafePointer<CChar>?) {
	windows[id]?.title = string(title)
}

@_cdecl("mk_window_size")
public func mk_window_size(_ id: Int32, _ width: Int32, _ height: Int32, _ minWidth: Int32, _ minHeight: Int32) {
	guard let window = windows[id] else { return }
	window.setContentSize(NSSize(width: Int(width), height: Int(height)))
	window.contentMinSize = NSSize(width: Int(minWidth), height: Int(minHeight))
}

// --- controls ---

/// Where a parent's children go.
private func content(of parent: NSView) -> NSView {
	if let scroll = parent as? MKScroll {
		return scroll.content
	}

	return parent
}

@_cdecl("mk_view_create")
public func mk_view_create(_ parent: Int32, _ kindValue: Int32) -> Int32 {
	guard let kind = Kind(rawValue: kindValue), let parentView = views[parent] else { return 0 }
	let id = newId()
	let view: NSView
	switch kind {
	case .container:
		let v = FlippedView()
		v.id = id
		view = v
	case .label, .link:
		let v = NSTextField(wrappingLabelWithString: "")
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.isSelectable = false
		v.lineBreakMode = .byWordWrapping
		if kind == .link {
			v.textColor = NSColor.linkColor
			let click = NSClickGestureRecognizer(target: Target.shared, action: #selector(Target.linkClicked(_:)))
			v.addGestureRecognizer(click)
		}

		view = v
	case .button, .toggle:
		let v = NSButton(title: "", target: Target.shared, action: #selector(Target.clicked(_:)))
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.bezelStyle = kind == .toggle ? .smallSquare : .rounded
		v.controlSize = .small
		if kind == .toggle {
			v.setButtonType(.pushOnPushOff)
		}

		view = v
	case .check:
		let v = NSButton(checkboxWithTitle: "", target: Target.shared, action: #selector(Target.clicked(_:)))
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.controlSize = .small
		view = v
	case .radio:
		let v = NSButton(radioButtonWithTitle: "", target: Target.shared, action: #selector(Target.clicked(_:)))
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.controlSize = .small
		view = v
	case .group:
		let v = MKGroup()
		v.id = id
		v.box.boxType = .primary
		v.box.titleFont = NSFont.systemFont(ofSize: 11)
		v.addSubview(v.box)
		view = v
	case .field:
		let v = NSTextField(string: "")
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.delegate = Target.shared
		v.controlSize = .small
		view = v
	case .area:
		let scroll = NSScrollView()
		scroll.hasVerticalScroller = true
		scroll.autohidesScrollers = true
		scroll.borderType = .bezelBorder
		let text = MKTextView()
		text.id = id
		text.font = NSFont.systemFont(ofSize: 11)
		text.isVerticallyResizable = true
		text.isHorizontallyResizable = false
		text.autoresizingMask = [.width]
		text.textContainer?.widthTracksTextView = true
		text.delegate = Target.shared
		scroll.documentView = text
		view = scroll
	case .popup:
		let v = NSPopUpButton(frame: .zero, pullsDown: false)
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.controlSize = .small
		v.target = Target.shared
		v.action = #selector(Target.selected(_:))
		view = v
	case .combo:
		let v = NSComboBox()
		v.tag = Int(id)
		v.font = NSFont.systemFont(ofSize: 11)
		v.controlSize = .small
		v.delegate = Target.shared
		view = v
	case .stepper:
		let v = MKStepper()
		v.id = id
		v.field.font = NSFont.systemFont(ofSize: 11)
		v.field.controlSize = .small
		v.field.delegate = Target.shared
		v.stepper.controlSize = .small
		v.stepper.target = Target.shared
		v.stepper.action = #selector(Target.stepped(_:))
		v.addSubview(v.field)
		v.addSubview(v.stepper)
		view = v
	case .slider:
		let v = NSSlider()
		v.tag = Int(id)
		v.target = Target.shared
		v.action = #selector(Target.slid(_:))
		v.isContinuous = true
		v.controlSize = .small
		view = v
	case .table:
		let v = MKTable()
		v.id = id
		v.setUp()
		view = v
	case .tabs:
		let v = MKTabs()
		v.id = id
		v.setUp()
		v.strip.tag = Int(id)
		view = v
	case .image:
		let v = MKImageView()
		v.id = id
		v.imageFrameStyle = .none
		v.isEditable = false
		view = v
	case .scroll:
		let v = MKScroll()
		v.id = id
		v.hasVerticalScroller = true
		v.autohidesScrollers = true
		v.drawsBackground = false
		v.documentView = v.content
		view = v
	}

	views[id] = view
	content(of: parentView).addSubview(view)
	return id
}

@_cdecl("mk_view_destroy")
public func mk_view_destroy(_ id: Int32) {
	views[id]?.removeFromSuperview()
	views[id] = nil
}

@_cdecl("mk_view_frame")
public func mk_view_frame(_ id: Int32, _ x: Int32, _ y: Int32, _ width: Int32, _ height: Int32) {
	views[id]?.frame = NSRect(x: Int(x), y: Int(y), width: Int(width), height: Int(height))
}

@_cdecl("mk_view_visible")
public func mk_view_visible(_ id: Int32, _ visible: Int32) {
	views[id]?.isHidden = visible == 0
}

@_cdecl("mk_view_enabled")
public func mk_view_enabled(_ id: Int32, _ enabled: Int32) {
	guard let view = views[id] else { return }
	if let control = view as? NSControl {
		control.isEnabled = enabled != 0
	} else if let stepper = view as? MKStepper {
		stepper.field.isEnabled = enabled != 0
		stepper.stepper.isEnabled = enabled != 0
	} else if let tabs = view as? MKTabs {
		tabs.strip.isEnabled = enabled != 0
	} else if let table = view as? MKTable {
		table.table.isEnabled = enabled != 0
	} else if let scroll = view as? NSScrollView, let text = scroll.documentView as? MKTextView {
		text.isSelectable = enabled != 0
		text.textColor = enabled != 0 ? NSColor.textColor : NSColor.disabledControlTextColor
	}
}

@_cdecl("mk_view_tooltip")
public func mk_view_tooltip(_ id: Int32, _ text: UnsafePointer<CChar>?) {
	let value = string(text)
	views[id]?.toolTip = value.isEmpty ? nil : value
}

@_cdecl("mk_view_fill")
public func mk_view_fill(_ id: Int32, _ red: Int32, _ green: Int32, _ blue: Int32, _ alpha: Int32) {
	if let view = views[id] as? FlippedView {
		view.fill = alpha == 0 ? nil : NSColor(srgbRed: CGFloat(red) / 255, green: CGFloat(green) / 255, blue: CGFloat(blue) / 255, alpha: CGFloat(alpha) / 255)
	}
}

/// A label's text colour and background (alpha 0: none).
@_cdecl("mk_view_colors")
public func mk_view_colors(_ id: Int32, _ red: Int32, _ green: Int32, _ blue: Int32, _ fillRed: Int32, _ fillGreen: Int32, _ fillBlue: Int32, _ fillAlpha: Int32) {
	guard let field = views[id] as? NSTextField else { return }
	field.textColor = NSColor(srgbRed: CGFloat(red) / 255, green: CGFloat(green) / 255, blue: CGFloat(blue) / 255, alpha: 1)
	field.drawsBackground = fillAlpha != 0
	if fillAlpha != 0 {
		field.backgroundColor = NSColor(srgbRed: CGFloat(fillRed) / 255, green: CGFloat(fillGreen) / 255, blue: CGFloat(fillBlue) / 255, alpha: CGFloat(fillAlpha) / 255)
	}
}

@_cdecl("mk_view_text")
public func mk_view_text(_ id: Int32, _ text: UnsafePointer<CChar>?) {
	let value = string(text)
	guard let view = views[id] else { return }
	if let button = view as? NSButton {
		button.title = value
	} else if let group = view as? MKGroup {
		group.box.title = value
		group.box.titlePosition = value.isEmpty ? .noTitle : .atTop
	} else if let combo = view as? NSComboBox {
		combo.stringValue = value
	} else if let field = view as? NSTextField {
		field.stringValue = value
	} else if let stepper = view as? MKStepper {
		stepper.field.stringValue = value
		stepper.stepper.doubleValue = Double(value) ?? stepper.stepper.doubleValue
	} else if let scroll = view as? NSScrollView, let area = scroll.documentView as? MKTextView {
		let attributes: [NSAttributedString.Key: Any] = [.font: area.font ?? NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.textColor]
		let text = NSMutableAttributedString(string: value, attributes: attributes)
		if area.isAutomaticLinkDetectionEnabled, let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) {
			for match in detector.matches(in: value, options: [], range: NSRange(location: 0, length: (value as NSString).length)) {
				if let url = match.url {
					text.addAttribute(.link, value: url, range: match.range)
				}
			}
		}

		area.textStorage?.setAttributedString(text)
	}
}

/// A label's or field's alignment: 0 left, 1 centre, 2 right.
@_cdecl("mk_view_align")
public func mk_view_align(_ id: Int32, _ alignment: Int32) {
	if let control = views[id] as? NSControl {
		control.alignment = alignment == 1 ? .center : alignment == 2 ? .right : .left
	}
}

@_cdecl("mk_view_font")
public func mk_view_font(_ id: Int32, _ size: Float, _ bold: Int32) {
	let font = bold != 0 ? NSFont.boldSystemFont(ofSize: CGFloat(size)) : NSFont.systemFont(ofSize: CGFloat(size))
	if let control = views[id] as? NSControl {
		control.font = font
	} else if let scroll = views[id] as? NSScrollView, let area = scroll.documentView as? MKTextView {
		area.font = font
	}
}

@_cdecl("mk_view_focus")
public func mk_view_focus(_ id: Int32) {
	if let view = views[id] {
		view.window?.makeFirstResponder(view)
	}
}

// --- particular controls ---

@_cdecl("mk_button_state")
public func mk_button_state(_ id: Int32, _ state: Int32) {
	if let button = views[id] as? NSButton {
		button.state = state == 2 ? .mixed : state == 1 ? .on : .off
	}
}

/// A button the Return (1) or Escape (2) key presses.
@_cdecl("mk_button_key")
public func mk_button_key(_ id: Int32, _ key: Int32) {
	if let button = views[id] as? NSButton {
		button.keyEquivalent = key == 1 ? "\r" : key == 2 ? "\u{1b}" : ""
	}
}

@_cdecl("mk_button_image")
public func mk_button_image(_ id: Int32, _ image: Int32) {
	if let button = views[id] as? NSButton {
		button.image = images[image]
		button.imagePosition = button.title.isEmpty ? .imageOnly : .imageLeading
	}
}

/// A popup's, editable combo's or tab strip's items.
@_cdecl("mk_items")
public func mk_items(_ id: Int32, _ list: UnsafePointer<UnsafePointer<CChar>?>?, _ count: Int32) {
	let items = strings(list, count)
	guard let view = views[id] else { return }
	if let popup = view as? NSPopUpButton {
		// Added as items, as addItems would merge those with the same text.
		popup.removeAllItems()
		for item in items {
			popup.menu?.addItem(NSMenuItem(title: item, action: nil, keyEquivalent: ""))
		}
	} else if let combo = view as? NSComboBox {
		combo.removeAllItems()
		combo.addItems(withObjectValues: items)
	} else if let tabs = view as? MKTabs {
		tabs.strip.segmentCount = items.count
		for (index, item) in items.enumerated() {
			tabs.strip.setLabel(item, forSegment: index)
		}

		tabs.needsLayout = true
	}
}

@_cdecl("mk_selected")
public func mk_selected(_ id: Int32, _ index: Int32) {
	guard let view = views[id] else { return }
	if let popup = view as? NSPopUpButton {
		popup.selectItem(at: Int(index))
	} else if let combo = view as? NSComboBox {
		combo.delegate = nil
		if index >= 0 {
			combo.selectItem(at: Int(index))
		} else if combo.indexOfSelectedItem >= 0 {
			combo.deselectItem(at: combo.indexOfSelectedItem)
		}

		combo.delegate = Target.shared
	} else if let tabs = view as? MKTabs {
		if index >= 0 && Int(index) < tabs.strip.segmentCount {
			tabs.strip.selectedSegment = Int(index)
		}
	}
}

@_cdecl("mk_slider")
public func mk_slider(_ id: Int32, _ minimum: Int32, _ maximum: Int32, _ value: Int32, _ ticks: Int32) {
	if let slider = views[id] as? NSSlider {
		slider.minValue = Double(minimum)
		slider.maxValue = Double(maximum)
		slider.integerValue = Int(value)
		slider.numberOfTickMarks = Int(ticks)
		slider.allowsTickMarkValuesOnly = ticks > 1
	}
}

@_cdecl("mk_stepper")
public func mk_stepper(_ id: Int32, _ minimum: Double, _ maximum: Double, _ value: Double, _ increment: Double, _ decimals: Int32) {
	if let view = views[id] as? MKStepper {
		view.decimals = decimals
		view.stepper.minValue = minimum
		view.stepper.maxValue = maximum
		view.stepper.increment = increment
		view.stepper.doubleValue = value
		view.field.stringValue = view.format(value)
	}
}

/// A text box's options: read only, and links in its text clickable (a rich text box).
@_cdecl("mk_text_options")
public func mk_text_options(_ id: Int32, _ readOnly: Int32, _ links: Int32) {
	guard let view = views[id] else { return }
	if let field = view as? NSTextField {
		field.isEditable = readOnly == 0
	} else if let scroll = view as? NSScrollView, let area = scroll.documentView as? MKTextView {
		area.isEditable = readOnly == 0
		area.isSelectable = true
		area.isAutomaticLinkDetectionEnabled = links != 0
	}
}

@_cdecl("mk_scroll_content")
public func mk_scroll_content(_ id: Int32, _ width: Int32, _ height: Int32) {
	if let scroll = views[id] as? MKScroll {
		scroll.content.frame = NSRect(x: 0, y: 0, width: Int(width), height: Int(height))
	}
}

// --- lists ---

@_cdecl("mk_table_columns")
public func mk_table_columns(_ id: Int32, _ names: UnsafePointer<UnsafePointer<CChar>?>?, _ widths: UnsafePointer<Int32>?, _ count: Int32, _ header: Int32) {
	guard let view = views[id] as? MKTable else { return }
	let titles = strings(names, count)
	for column in view.table.tableColumns {
		view.table.removeTableColumn(column)
	}

	for (index, title) in titles.enumerated() {
		let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(String(index)))
		column.title = title
		column.width = CGFloat(widths?[index] ?? 100)
		view.table.addTableColumn(column)
	}

	view.table.headerView = header != 0 ? NSTableHeaderView() : nil
	view.table.columnAutoresizingStyle = titles.count == 1 ? .lastColumnOnlyAutoresizingStyle : .noColumnAutoresizing
}

/// A list's rows: count rows of columns cells, row by row; an icon (an image's id, 0 for none) a row.
@_cdecl("mk_table_rows")
public func mk_table_rows(_ id: Int32, _ cells: UnsafePointer<UnsafePointer<CChar>?>?, _ rows: Int32, _ columns: Int32, _ icons: UnsafePointer<Int32>?) {
	guard let view = views[id] as? MKTable else { return }
	var data: [[String]] = []
	data.reserveCapacity(Int(rows))
	for row in 0..<Int(rows) {
		var line: [String] = []
		line.reserveCapacity(Int(columns))
		for column in 0..<Int(columns) {
			line.append(string(cells?[row * Int(columns) + column]))
		}

		data.append(line)
	}

	view.rows = data
	view.icons = icons == nil ? [] : (0..<Int(rows)).map { icons![$0] }
	view.quiet = true
	view.table.reloadData()
	view.quiet = false
}

@_cdecl("mk_table_selection")
public func mk_table_selection(_ id: Int32, _ selected: UnsafePointer<Int32>?, _ count: Int32, _ multiple: Int32) {
	guard let view = views[id] as? MKTable else { return }
	view.table.allowsMultipleSelection = multiple != 0
	var indexes = IndexSet()
	for index in 0..<Int(count) {
		indexes.insert(Int(selected![index]))
	}

	view.quiet = true
	view.table.selectRowIndexes(indexes, byExtendingSelection: false)
	view.quiet = false
}

@_cdecl("mk_table_show_row")
public func mk_table_show_row(_ id: Int32, _ row: Int32) {
	(views[id] as? MKTable)?.table.scrollRowToVisible(Int(row))
}

// --- pictures ---

/// A picture from premultiplied BGRA pixels (System.Drawing's Format32bppPArgb), drawn at so many pixels a point.
@_cdecl("mk_image_create")
public func mk_image_create(_ pixels: UnsafeRawPointer?, _ width: Int32, _ height: Int32, _ stride: Int32, _ scale: Float) -> Int32 {
	guard let pixels = pixels, width > 0, height > 0 else { return 0 }
	let data = Data(bytes: pixels, count: Int(stride) * Int(height))
	guard let provider = CGDataProvider(data: data as CFData) else { return 0 }
	let info = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
	guard let image = CGImage(width: Int(width), height: Int(height), bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: Int(stride), space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: info, provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { return 0 }
	let id = newId()
	images[id] = NSImage(cgImage: image, size: NSSize(width: CGFloat(width) / CGFloat(scale), height: CGFloat(height) / CGFloat(scale)))
	return id
}

@_cdecl("mk_image_free")
public func mk_image_free(_ id: Int32) {
	images[id] = nil
}

/// A picture view's picture and how it fits: 0 at its size from the top left, 1 stretched, 2 zoomed, 3 centred.
@_cdecl("mk_image_set")
public func mk_image_set(_ id: Int32, _ image: Int32, _ mode: Int32) {
	guard let view = views[id] as? NSImageView else { return }
	view.image = images[image]
	switch mode {
	case 1:
		view.imageScaling = .scaleAxesIndependently
	case 2:
		view.imageScaling = .scaleProportionallyUpOrDown
	case 3:
		view.imageScaling = .scaleNone
		view.imageAlignment = .alignCenter
	default:
		view.imageScaling = .scaleNone
		view.imageAlignment = .alignTopLeft
	}
}

// --- dialogs ---

/// A message: the index of the button pressed. style: 0 information, 1 warning, 2 critical.
@_cdecl("mk_alert")
public func mk_alert(_ title: UnsafePointer<CChar>?, _ text: UnsafePointer<CChar>?, _ buttons: UnsafePointer<UnsafePointer<CChar>?>?, _ count: Int32, _ style: Int32) -> Int32 {
	let alert = NSAlert()
	alert.messageText = string(title)
	alert.informativeText = string(text)
	alert.alertStyle = style == 2 ? .critical : style == 1 ? .warning : .informational
	for label in strings(buttons, count) {
		alert.addButton(withTitle: label)
	}

	let response = alert.runModal()
	return Int32(response.rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue)
}

private func copied(_ text: String) -> UnsafeMutablePointer<CChar>? {
	return strdup(text)
}

@_cdecl("mk_free")
public func mk_free(_ text: UnsafeMutablePointer<CChar>?) {
	free(text)
}

private func apply(_ extensions: [String], to panel: NSSavePanel) {
	// "*" or none: any file.
	if !extensions.isEmpty && !extensions.contains("*") {
		panel.allowedFileTypes = extensions
	}
}

/// Chooses files or a folder: their paths, a line each, or nothing if cancelled (free with mk_free).
@_cdecl("mk_open_panel")
public func mk_open_panel(_ title: UnsafePointer<CChar>?, _ folder: UnsafePointer<CChar>?, _ extensions: UnsafePointer<UnsafePointer<CChar>?>?, _ count: Int32, _ multiple: Int32, _ folders: Int32) -> UnsafeMutablePointer<CChar>? {
	let panel = NSOpenPanel()
	panel.message = string(title)
	panel.canChooseFiles = folders == 0
	panel.canChooseDirectories = folders != 0
	panel.canCreateDirectories = folders != 0
	panel.allowsMultipleSelection = multiple != 0
	let start = string(folder)
	if !start.isEmpty {
		panel.directoryURL = URL(fileURLWithPath: start)
	}

	apply(strings(extensions, count), to: panel)
	if panel.runModal() != .OK {
		return nil
	}

	return copied(panel.urls.map { $0.path }.joined(separator: "\n"))
}

/// Chooses where to save: the path, or nothing if cancelled (free with mk_free).
@_cdecl("mk_save_panel")
public func mk_save_panel(_ title: UnsafePointer<CChar>?, _ folder: UnsafePointer<CChar>?, _ name: UnsafePointer<CChar>?, _ extensions: UnsafePointer<UnsafePointer<CChar>?>?, _ count: Int32) -> UnsafeMutablePointer<CChar>? {
	let panel = NSSavePanel()
	panel.message = string(title)
	panel.canCreateDirectories = true
	panel.nameFieldStringValue = string(name)
	let start = string(folder)
	if !start.isEmpty {
		panel.directoryURL = URL(fileURLWithPath: start)
	}

	apply(strings(extensions, count), to: panel)
	if panel.runModal() != .OK {
		return nil
	}

	return panel.url == nil ? nil : copied(panel.url!.path)
}

/// A context menu at a place in a control: the index of the item chosen, or -1. An empty item is a separator.
@_cdecl("mk_menu_popup")
public func mk_menu_popup(_ id: Int32, _ x: Int32, _ y: Int32, _ items: UnsafePointer<UnsafePointer<CChar>?>?, _ enabled: UnsafePointer<Int32>?, _ count: Int32) -> Int32 {
	guard let view = views[id] else { return -1 }
	let menu = NSMenu()
	menu.autoenablesItems = false
	let chooser = MenuChooser()
	for (index, title) in strings(items, count).enumerated() {
		if title.isEmpty {
			menu.addItem(NSMenuItem.separator())
			continue
		}

		let item = NSMenuItem(title: title, action: #selector(MenuChooser.chosen(_:)), keyEquivalent: "")
		item.target = chooser
		item.tag = index
		item.isEnabled = enabled?[index] != 0
		menu.addItem(item)
	}

	menu.popUp(positioning: nil, at: NSPoint(x: Int(x), y: Int(y)), in: view)
	return Int32(chooser.choice)
}

private class MenuChooser: NSObject {
	var choice = -1

	@objc func chosen(_ sender: NSMenuItem) {
		choice = sender.tag
	}
}

extension Target {
	@objc func linkClicked(_ sender: NSGestureRecognizer) {
		if let field = sender.view as? NSTextField {
			emit(Int32(field.tag), .link)
		}
	}
}
