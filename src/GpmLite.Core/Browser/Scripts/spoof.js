// Injected into every page, frame and worker before any site script runs.
// `__CFG__` is replaced by the launcher with the profile's spoof config (JSON).
(function (cfg) {
  'use strict';
  if (!cfg) return;
  const G = globalThis;
  // No global "already applied" marker: it would be detectable. The launcher injects once per context.

  // ---------- native-looking patches ----------
  const nativeToString = Function.prototype.toString;
  const masked = new WeakMap();
  const tsProxy = new Proxy(nativeToString, {
    apply(target, thisArg, args) {
      if (masked.has(thisArg)) return masked.get(thisArg);
      return Reflect.apply(target, thisArg, args);
    },
  });
  masked.set(tsProxy, 'function toString() { [native code] }');
  Object.defineProperty(Function.prototype, 'toString', { value: tsProxy, writable: true, configurable: true, enumerable: false });

  const mask = (fn, name) => { masked.set(fn, `function ${name}() { [native code] }`); return fn; };

  /** Replace a method on a prototype with a Proxy so name/length/toString stay native. */
  function hookMethod(proto, name, makeApply) {
    if (!proto) return;
    const desc = Object.getOwnPropertyDescriptor(proto, name);
    if (!desc || typeof desc.value !== 'function') return;
    const orig = desc.value;
    const p = new Proxy(orig, { apply: makeApply(orig) });
    masked.set(p, `function ${name}() { [native code] }`);
    Object.defineProperty(proto, name, { ...desc, value: p });
  }

  /** Override a prototype getter with a fixed value (falls back to defining one). */
  function hookGetter(proto, name, value) {
    if (!proto) return;
    const desc = Object.getOwnPropertyDescriptor(proto, name);
    const getter = { get [name]() { return value; } };
    const g = Object.getOwnPropertyDescriptor(getter, name).get;
    mask(g, `get ${name}`);
    Object.defineProperty(proto, name, {
      get: g, set: desc ? desc.set : undefined,
      enumerable: desc ? desc.enumerable : true, configurable: true,
    });
  }

  // ---------- deterministic noise ----------
  const seed = (cfg.seed >>> 0) || 1;
  function hash32(a, b) {
    let h = (a ^ 0x9e3779b9) >>> 0;
    h = Math.imul(h ^ (b >>> 0), 0x85ebca6b);
    h ^= h >>> 13;
    h = Math.imul(h, 0xc2b2ae35);
    h ^= h >>> 16;
    return h >>> 0;
  }
  const saltCanvas = hash32(seed, 1), saltWebgl = hash32(seed, 2), saltAudio = hash32(seed, 3), saltRect = hash32(seed, 4);

  /** Flip the low bit of ~2% of opaque-ish pixels. Same input + same seed = same output. */
  // (x0, y0, w, fullW) map the buffer back to absolute canvas coordinates, so a sub-region read and a
  // full-canvas export of the same pixels get identical noise.
  function noisePixels(data, salt, x0 = 0, y0 = 0, w = 0, fullW = 0) {
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] === 0) continue; // keep transparent pixels untouched (blank canvases stay blank)
      const px = i >>> 2;
      const abs = w > 0 && fullW > 0 ? (y0 + Math.floor(px / w)) * fullW + x0 + (px % w) : px;
      const h = hash32(salt, abs);
      if ((h & 0xff) < 5) {
        const ch = (h >>> 8) % 3;
        data[i + ch] = data[i + ch] ^ 1;
      }
    }
  }

  // ---------- navigator ----------
  const isWorker = typeof WorkerGlobalScope !== 'undefined' && G instanceof WorkerGlobalScope;
  const NavProto = isWorker ? (G.WorkerNavigator && WorkerNavigator.prototype) : (G.Navigator && Navigator.prototype);
  if (cfg.hardwareConcurrency) hookGetter(NavProto, 'hardwareConcurrency', cfg.hardwareConcurrency);
  if (cfg.deviceMemory) hookGetter(NavProto, 'deviceMemory', cfg.deviceMemory);

  // ---------- screen ----------
  if (!isWorker && G.Screen && cfg.screen) {
    const S = Screen.prototype, sc = cfg.screen;
    hookGetter(S, 'availWidth', sc.availWidth);
    hookGetter(S, 'availHeight', sc.availHeight);
    hookGetter(S, 'colorDepth', sc.colorDepth);
    hookGetter(S, 'pixelDepth', sc.colorDepth);
  }

  // ---------- canvas 2D ----------
  if (cfg.canvas === 'Noise' && G.HTMLCanvasElement) {
    const C2D = G.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype;
    const origGetImageData = C2D && C2D.getImageData;
    const noisedCopy = (canvas) => {
      const w = canvas.width, h = canvas.height;
      if (!w || !h) return null;
      const copy = document.createElement('canvas');
      copy.width = w; copy.height = h;
      const ctx = copy.getContext('2d');
      ctx.drawImage(canvas, 0, 0);
      const img = origGetImageData.call(ctx, 0, 0, w, h);
      noisePixels(img.data, saltCanvas, 0, 0, w, w);
      ctx.putImageData(img, 0, 0);
      return copy;
    };
    hookMethod(HTMLCanvasElement.prototype, 'toDataURL', (orig) => (target, self, args) => {
      try { const c = noisedCopy(self); if (c) return Reflect.apply(orig, c, args); } catch (_) {}
      return Reflect.apply(orig, self, args);
    });
    hookMethod(HTMLCanvasElement.prototype, 'toBlob', (orig) => (target, self, args) => {
      try { const c = noisedCopy(self); if (c) return Reflect.apply(orig, c, args); } catch (_) {}
      return Reflect.apply(orig, self, args);
    });
    hookMethod(C2D, 'getImageData', (orig) => (target, self, args) => {
      const img = Reflect.apply(orig, self, args);
      try {
        const sx = Math.trunc(args[0]) || 0, sy = Math.trunc(args[1]) || 0;
        noisePixels(img.data, saltCanvas, sx, sy, img.width, self.canvas ? self.canvas.width : 0);
      } catch (_) {}
      return img;
    });
  }

  // ---------- WebGL ----------
  const glProtos = [G.WebGLRenderingContext, G.WebGL2RenderingContext].filter(Boolean).map((c) => c.prototype);
  for (const P of glProtos) {
    if (cfg.webglMeta && cfg.webglVendor) {
      hookMethod(P, 'getParameter', (orig) => (target, self, args) => {
        const p = args[0];
        if (p === 0x9245) return cfg.webglVendor;   // UNMASKED_VENDOR_WEBGL
        if (p === 0x9246) return cfg.webglRenderer; // UNMASKED_RENDERER_WEBGL
        return Reflect.apply(orig, self, args);
      });
    }
    if (cfg.webglImage === 'Noise') {
      hookMethod(P, 'readPixels', (orig) => (target, self, args) => {
        const r = Reflect.apply(orig, self, args);
        try {
          const out = args[6];
          if (out && out.length && out instanceof Uint8Array) noisePixels(out, saltWebgl);
        } catch (_) {}
        return r;
      });
    }
  }

  // ---------- audio ----------
  if (cfg.audio === 'Noise') {
    const done = new WeakSet();
    if (G.AudioBuffer) {
      hookMethod(AudioBuffer.prototype, 'getChannelData', (orig) => (target, self, args) => {
        const data = Reflect.apply(orig, self, args);
        try {
          if (!done.has(data)) {
            done.add(data);
            for (let i = 0; i < data.length; i += 97) {
              const h = hash32(saltAudio, i);
              data[i] += ((h & 0xffff) / 0xffff - 0.5) * 1e-7;
            }
          }
        } catch (_) {}
        return data;
      });
    }
    if (G.AnalyserNode) {
      hookMethod(AnalyserNode.prototype, 'getFloatFrequencyData', (orig) => (target, self, args) => {
        const r = Reflect.apply(orig, self, args);
        try {
          const arr = args[0];
          for (let i = 0; i < arr.length; i++) arr[i] += ((hash32(saltAudio, i) & 0xff) / 0xff - 0.5) * 1e-4;
        } catch (_) {}
        return r;
      });
    }
  }

  // ---------- client rects ----------
  if (cfg.clientRects === 'Noise' && G.DOMRect && G.Element) {
    const jitter = (v, k) => v + ((hash32(saltRect, (Math.round(v * 100) ^ k) >>> 0) & 0xff) / 0xff - 0.5) * 1e-4;
    const fuzz = (r) => new DOMRect(jitter(r.x, 1), jitter(r.y, 2), jitter(r.width, 3), jitter(r.height, 4));
    for (const P of [Element.prototype, G.Range && Range.prototype].filter(Boolean)) {
      hookMethod(P, 'getBoundingClientRect', (orig) => (target, self, args) => {
        const r = Reflect.apply(orig, self, args);
        try { return fuzz(r); } catch (_) { return r; }
      });
    }
  }

  // ---------- media devices ----------
  if (!isWorker && cfg.media && G.MediaDevices) {
    hookMethod(MediaDevices.prototype, 'enumerateDevices', (orig) => (target, self, args) =>
      Reflect.apply(orig, self, args).then((list) => {
        const want = { audioinput: cfg.media.audioInputs, audiooutput: cfg.media.audioOutputs, videoinput: cfg.media.videoInputs };
        const out = [];
        for (const kind of ['audioinput', 'videoinput', 'audiooutput']) {
          const real = list.filter((d) => d.kind === kind);
          if (!real.length) continue; // cannot fabricate a device type the host doesn't have
          for (let i = 0; i < want[kind]; i++) {
            const base = real[i % real.length];
            if (i < real.length) { out.push(base); continue; }
            const fakeId = base.deviceId ? hash32(seed, i * 31 + kind.length).toString(16).padStart(8, '0').repeat(8) : '';
            out.push(new Proxy(base, {
              get(t, prop) {
                if (prop === 'deviceId') return fakeId;
                if (prop === 'label') return t.label ? `${t.label} (${i + 1})` : '';
                const v = Reflect.get(t, prop, t);
                return typeof v === 'function' ? v.bind(t) : v;
              },
            }));
          }
        }
        return out;
      }));
  }

  // ---------- WebRTC ----------
  if (!isWorker && cfg.webrtc === 'Disabled') {
    for (const n of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCSessionDescription', 'RTCIceCandidate']) {
      try { delete G[n]; } catch (_) {}
    }
  } else if (!isWorker && cfg.webrtc === 'Altered' && cfg.webrtcIp && G.RTCIceCandidate) {
    const ipv4 = /\b(?!0\.0\.0\.0\b)(?!127\.)(\d{1,3}(?:\.\d{1,3}){3})\b/g;
    const mdns = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.local\b/gi;
    const swap = (s) => typeof s === 'string' ? s.replace(ipv4, cfg.webrtcIp).replace(mdns, cfg.webrtcIp) : s;
    for (const [proto, props] of [[RTCIceCandidate.prototype, ['candidate', 'address']], [G.RTCSessionDescription && RTCSessionDescription.prototype, ['sdp']]]) {
      if (!proto) continue;
      for (const prop of props) {
        const d = Object.getOwnPropertyDescriptor(proto, prop);
        if (!d || !d.get) continue;
        const origGet = d.get;
        const getter = { get [prop]() { return swap(origGet.call(this)); } };
        const g = mask(Object.getOwnPropertyDescriptor(getter, prop).get, `get ${prop}`);
        Object.defineProperty(proto, prop, { ...d, get: g });
      }
    }
  }
})(__CFG__);
