/*
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

  Web dashboard for Open Hardware Monitor.

  Replaces the 2012 page built on jQuery 1.7.2, jQuery UI 1.8.16 and
  Knockout 2.1.0. That page inserted hardware and sensor names into the DOM
  as HTML, and sensor names are user-editable, so a renamed sensor could
  inject script. Everything here is written with textContent, the page is
  served under a strict Content-Security-Policy, and there are no
  third-party dependencies.

  Reads /api/v1/sensors, whose readings are plain numbers in canonical units.
*/

'use strict';

(() => {
  const params = new URLSearchParams(window.location.search);
  const token = params.get('token');
  const withToken = (path) =>
    token ? `${path}?token=${encodeURIComponent(token)}` : path;

  const byId = (id) => document.getElementById(id);
  const storage = {
    get(key, fallback) {
      try {
        const value = window.localStorage.getItem(key);
        return value === null ? fallback : value;
      } catch {
        return fallback;
      }
    },
    set(key, value) {
      try { window.localStorage.setItem(key, value); } catch { /* private mode */ }
    }
  };

  const TYPE_ORDER = ['Temperature', 'Load', 'Clock', 'Power', 'Voltage', 'Fan',
    'Control', 'Level', 'Flow', 'SmallData', 'Data', 'Throughput', 'Factor'];
  const TYPE_LABEL = {
    Temperature: 'Temperatures', Load: 'Load', Clock: 'Clocks', Power: 'Power',
    Voltage: 'Voltages', Fan: 'Fans', Control: 'Controls', Level: 'Levels',
    Flow: 'Flow', SmallData: 'Memory', Data: 'Data', Throughput: 'Throughput',
    Factor: 'Counters'
  };
  const DECIMALS = {
    Temperature: 1, Load: 1, Clock: 0, Power: 1, Voltage: 3, Fan: 0,
    Control: 1, Level: 1, Flow: 0, SmallData: 0, Data: 2, Throughput: 0,
    Factor: 0
  };
  const HARDWARE_ICON = {
    CPU: 'cpu.png', GpuNvidia: 'nvidia.png', GpuAti: 'ati.png', HDD: 'hdd.png',
    Mainboard: 'mainboard.png', SuperIO: 'chip.png', RAM: 'ram.png',
    Heatmaster: 'bigng.png', TBalancer: 'bigng.png'
  };

  const ui = {
    machine: byId('machine'),
    status: byId('status'),
    tier: byId('tier'),
    hardware: byId('hardware'),
    filter: byId('filter'),
    interval: byId('interval'),
    unit: byId('unit')
  };

  byId('api-link').href = withToken('api/v1/sensors');
  byId('legacy-link').href = withToken('data.json');

  let fahrenheit = storage.get('ohm.unit', 'C') === 'F';
  let intervalMs = Number(storage.get('ohm.interval', '2000')) || 2000;
  let filterText = '';
  let timer = 0;
  let lastData = null;

  // hardware id -> { details, parentLabel, groups: Map(type -> tbody), rows: Map(sensor id -> row) }
  const cards = new Map();

  function formatNumber(value, decimals) {
    return value.toLocaleString(undefined, {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals
    });
  }

  function humanBytesPerSecond(value) {
    const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
    let i = 0;
    while (Math.abs(value) >= 1024 && i < units.length - 1) {
      value /= 1024;
      i++;
    }
    return `${formatNumber(value, i === 0 ? 0 : 1)} ${units[i]}`;
  }

  function format(sensor, value) {
    if (value === null || value === undefined || Number.isNaN(value))
      return '—';
    let unit = sensor.unit || '';
    if (sensor.type === 'Temperature' && fahrenheit) {
      value = value * 9 / 5 + 32;
      unit = '°F';
    }
    if (sensor.type === 'Throughput' && unit === 'MB/s')
      return humanBytesPerSecond(value * 1048576);
    const text = formatNumber(value, DECIMALS[sensor.type] ?? 2);
    return unit ? `${text} ${unit}` : text;
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function createCard(hardware) {
    const details = element('details', 'hardware');
    details.dataset.id = hardware.id;
    details.open = storage.get(`ohm.collapsed:${hardware.id}`, '0') !== '1';
    details.addEventListener('toggle', () =>
      storage.set(`ohm.collapsed:${hardware.id}`, details.open ? '0' : '1'));

    const summary = element('summary');
    const icon = element('img');
    icon.alt = '';
    icon.src = `images_icon/${HARDWARE_ICON[hardware.type] || 'chip.png'}`;
    const name = element('span', 'hardware-name');
    const parentLabel = element('span', 'hardware-parent');
    summary.append(icon, name, parentLabel);

    const wrap = element('div', 'table-wrap');
    const table = element('table');
    const head = element('thead');
    const headRow = element('tr');
    headRow.append(element('th', '', 'Sensor'), element('th', 'num', 'Value'),
      element('th', 'num', 'Min'), element('th', 'num', 'Max'));
    head.append(headRow);
    table.append(head);
    wrap.append(table);
    details.append(summary, wrap);

    return { details, name, parentLabel, table, groups: new Map(), rows: new Map() };
  }

  function groupBody(card, type) {
    let body = card.groups.get(type);
    if (body) return body;

    body = element('tbody');
    body.dataset.type = type;
    const heading = element('tr', 'group');
    const cell = element('th', '', TYPE_LABEL[type] || type);
    cell.colSpan = 4;
    heading.append(cell);
    body.append(heading);

    // Keep groups in a fixed order regardless of arrival order.
    const rank = TYPE_ORDER.indexOf(type);
    const next = [...card.groups.entries()]
      .filter(([other]) => TYPE_ORDER.indexOf(other) > rank)
      .sort((a, b) => TYPE_ORDER.indexOf(a[0]) - TYPE_ORDER.indexOf(b[0]))[0];
    card.table.insertBefore(body, next ? next[1] : null);
    card.groups.set(type, body);
    return body;
  }

  function createRow(sensor) {
    const tr = element('tr');
    const name = element('td', 'name');
    const value = element('td', 'num');
    const min = element('td', 'num');
    const max = element('td', 'num');
    tr.append(name, value, min, max);
    return { tr, name, value, min, max };
  }

  function render(data) {
    lastData = data;
    ui.machine.textContent = data.machine || 'Open Hardware Monitor';
    document.title = data.machine
      ? `${data.machine} — Open Hardware Monitor`
      : 'Open Hardware Monitor';

    if (data.accessNote) {
      ui.tier.textContent = data.accessNote;
      ui.tier.hidden = false;
    } else {
      ui.tier.hidden = true;
    }

    const names = new Map(data.hardware.map((h) => [h.id, h.name]));
    const seenHardware = new Set();
    let order = 0;

    for (const hardware of data.hardware) {
      seenHardware.add(hardware.id);
      let card = cards.get(hardware.id);
      if (!card) {
        card = createCard(hardware);
        cards.set(hardware.id, card);
      }
      // Re-append in API order; appendChild moves an existing node.
      if (ui.hardware.children[order] !== card.details)
        ui.hardware.insertBefore(card.details, ui.hardware.children[order] || null);
      order++;

      card.name.textContent = hardware.name;
      card.name.title = hardware.name;
      card.parentLabel.textContent =
        hardware.parentId && names.has(hardware.parentId) ? names.get(hardware.parentId) : '';

      const seenSensors = new Set();
      const sorted = [...hardware.sensors].sort((a, b) =>
        (TYPE_ORDER.indexOf(a.type) - TYPE_ORDER.indexOf(b.type)) || (a.index - b.index));

      for (const sensor of sorted) {
        seenSensors.add(sensor.id);
        let row = card.rows.get(sensor.id);
        const body = groupBody(card, sensor.type);
        if (!row) {
          row = createRow(sensor);
          card.rows.set(sensor.id, row);
        }
        if (row.tr.parentNode !== body) body.append(row.tr);
        row.sensor = sensor;
        row.name.textContent = sensor.name;
        row.name.title = sensor.name;
        row.value.textContent = format(sensor, sensor.value);
        row.min.textContent = format(sensor, sensor.min);
        row.max.textContent = format(sensor, sensor.max);
      }

      for (const [id, row] of card.rows) {
        if (!seenSensors.has(id)) {
          row.tr.remove();
          card.rows.delete(id);
        }
      }
      for (const [type, body] of card.groups) {
        if (body.rows.length <= 1) {
          body.remove();
          card.groups.delete(type);
        }
      }
    }

    for (const [id, card] of cards) {
      if (!seenHardware.has(id)) {
        card.details.remove();
        cards.delete(id);
      }
    }

    applyFilter();
  }

  function applyFilter() {
    const needle = filterText.trim().toLowerCase();
    let anyVisible = false;

    for (const card of cards.values()) {
      let cardVisible = false;
      const hardwareMatches = needle && card.name.textContent.toLowerCase().includes(needle);
      for (const body of card.groups.values()) {
        let groupVisible = false;
        for (const tr of [...body.rows].slice(1)) {
          const match = !needle || hardwareMatches ||
            tr.cells[0].textContent.toLowerCase().includes(needle);
          tr.hidden = !match;
          groupVisible = groupVisible || match;
        }
        body.hidden = !groupVisible;
        cardVisible = cardVisible || groupVisible;
      }
      card.details.hidden = !cardVisible && !(hardwareMatches && card.rows.size === 0);
      anyVisible = anyVisible || !card.details.hidden;
    }

    let empty = ui.hardware.querySelector('.empty');
    if (!anyVisible && lastData) {
      if (!empty) {
        empty = element('p', 'empty');
        ui.hardware.append(empty);
      }
      empty.textContent = needle ? 'No sensors match the filter.' : 'No sensors reported.';
    } else if (empty) {
      empty.remove();
    }
  }

  function setStatus(text, isError) {
    ui.status.textContent = text;
    ui.status.classList.toggle('error', Boolean(isError));
  }

  async function poll() {
    clearTimeout(timer);
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), Math.max(4000, intervalMs));
    try {
      const response = await fetch(withToken('api/v1/sensors'), {
        cache: 'no-store',
        signal: controller.signal
      });
      if (response.status === 401 || response.status === 403) {
        setStatus('Access denied — open the link that includes the access token.', true);
        return;
      }
      if (!response.ok)
        throw new Error(`HTTP ${response.status}`);
      render(await response.json());
      setStatus(`Updated ${new Date().toLocaleTimeString()}`, false);
    } catch (error) {
      setStatus('Connection lost — retrying…', true);
    } finally {
      clearTimeout(timeout);
      timer = setTimeout(poll, intervalMs);
    }
  }

  function refreshUnitButton() {
    ui.unit.textContent = fahrenheit ? '°F' : '°C';
    ui.unit.setAttribute('aria-label',
      fahrenheit ? 'Showing Fahrenheit; switch to Celsius' : 'Showing Celsius; switch to Fahrenheit');
  }

  ui.interval.value = String(intervalMs);
  if (ui.interval.value !== String(intervalMs)) {
    intervalMs = 2000;
    ui.interval.value = '2000';
  }
  ui.interval.addEventListener('change', () => {
    intervalMs = Number(ui.interval.value) || 2000;
    storage.set('ohm.interval', String(intervalMs));
    poll();
  });

  ui.filter.addEventListener('input', () => {
    filterText = ui.filter.value;
    applyFilter();
  });

  ui.unit.addEventListener('click', () => {
    fahrenheit = !fahrenheit;
    storage.set('ohm.unit', fahrenheit ? 'F' : 'C');
    refreshUnitButton();
    if (lastData) render(lastData);
  });

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) poll();
  });

  refreshUnitButton();
  poll();
})();
