// Drive the public form controls without a browser or a second Decky connection.
export function createGameAdderHarness(factory, env, api, router = {}) {
  let state, mounted = false, effect, cleanup;
  const React = {
    createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
    useState: initial => {
      if (!mounted) state = typeof initial === 'function' ? initial() : initial;
      return [state, next => { state = next; }];
    },
    useMemo: make => make(),
    useEffect: callback => { if (!mounted) effect = callback; },
  };
  const ui = Object.fromEntries(['PanelSection', 'PanelSectionRow', 'ButtonItem', 'TextField', 'DropdownItem'].map(key => [key, key]));
  ui.Router = router;
  const GameAdder = factory(env, ui, api, React);
  function render() {
    const tree = GameAdder();
    if (!mounted) { mounted = true; cleanup = effect?.(); }
    return tree;
  }
  function nodes(value, found = []) {
    if (Array.isArray(value)) value.forEach(child => nodes(child, found));
    else if (value && typeof value === 'object') { found.push(value); nodes(value.children, found); }
    return found;
  }
  function text(value) {
    if (Array.isArray(value)) return value.map(text).join('');
    if (value && typeof value === 'object') return text(value.children);
    return value == null || value === false ? '' : String(value);
  }
  function control(type, label) {
    return nodes(render()).find(node => node.type === type && (node.props.label === label || text(node) === label));
  }
  return {
    render, text: () => text(render()),
    button: label => control('ButtonItem', label),
    field: label => control('TextField', label),
    dropdown: label => control('DropdownItem', label),
    async click(label) {
      const button = control('ButtonItem', label);
      if (!button || button.props.disabled) throw new Error('Button unavailable: ' + label);
      await button.props.onClick();
    },
    change(label, value) {
      const field = control('TextField', label);
      if (!field || field.props.disabled) throw new Error('Field unavailable: ' + label);
      field.props.onChange({ target: { value } });
    },
    select(label, data) {
      const dropdown = control('DropdownItem', label);
      if (!dropdown || dropdown.props.disabled) throw new Error('Dropdown unavailable: ' + label);
      dropdown.props.onChange({ data });
    },
    async ready() {
      render();
      for (let attempt = 0; attempt < 100; attempt++) {
        if (!control('ButtonItem', '选择游戏启动文件')?.props.disabled) return;
        await new Promise(resolve => setTimeout(resolve, 20));
      }
      throw new Error('Form initialization timed out');
    },
    unmount() { cleanup?.(); cleanup = undefined; mounted = false; },
  };
}
