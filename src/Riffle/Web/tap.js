// Runs inside SoundCloud's embed. The player already routes its audio through Web Audio,
// so this slots the equalizer and the level meter in front of the speakers. Nothing is
// recorded or saved, and when the tap can't attach the song still plays untouched.
if (location.hostname === 'w.soundcloud.com') {
  (() => {
    const bandsHz = [80, 250, 1000, 4000, 12000];
    const realConnect = AudioNode.prototype.connect;
    const chains = new WeakMap();
    let settings = { eq: [0, 0, 0, 0, 0], level: false };
    let chain = null;

    const send = m => window.parent.postMessage(Object.assign({ riffle: 1 }, m), '*');

    function build(ctx) {
      const pre = ctx.createGain();
      const auto = ctx.createGain();
      const filters = bandsHz.map((hz, i) => {
        const f = ctx.createBiquadFilter();
        f.type = i === 0 ? 'lowshelf' : i === bandsHz.length - 1 ? 'highshelf' : 'peaking';
        f.frequency.value = hz;
        f.Q.value = 0.9;
        return f;
      });
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 1024;
      analyser.smoothingTimeConstant = 0.72;
      analyser.minDecibels = -85;
      analyser.maxDecibels = -18;

      let node = pre;
      for (const n of [auto, ...filters, analyser]) { realConnect.call(node, n); node = n; }
      realConnect.call(analyser, ctx.destination);
      return { ctx, pre, auto, filters, analyser };
    }

    function apply() {
      if (!chain) return;
      settings.eq.forEach((db, i) => { chain.filters[i].gain.value = db; });
      // Boosting a band can't overload anything because the whole signal drops by the
      // same amount first. The extra 1 dB leaves room for compressed audio, which can
      // overshoot full scale when decoded and would otherwise distort at high volume.
      const boost = Math.max(0, ...settings.eq);
      chain.pre.gain.value = Math.pow(10, -(boost + 1) / 20);
      if (!settings.level) chain.auto.gain.setTargetAtTime(1, chain.ctx.currentTime, 0.3);
    }

    function meter() {
      const bins = new Uint8Array(chain.analyser.frequencyBinCount);
      const wave = new Float32Array(chain.analyser.fftSize);
      const edges = [];
      const count = 24;
      const binHz = chain.ctx.sampleRate / chain.analyser.fftSize;
      for (let b = 0; b <= count; b++)
        edges.push(Math.max(b, Math.round(50 * Math.pow(14000 / 50, b / count) / binHz)));

      let loud = 0;
      setInterval(() => {
        chain.analyser.getByteFrequencyData(bins);
        const bands = [];
        let any = 0;
        for (let b = 0; b < count; b++) {
          let peak = 0, sum = 0, n = 0;
          for (let k = edges[b]; k < Math.max(edges[b] + 1, edges[b + 1]) && k < bins.length; k++) {
            peak = Math.max(peak, bins[k]); sum += bins[k]; n++;
          }
          const v = n ? (peak + sum / n) / 2 / 255 : 0;
          bands.push(Math.round(v * 100) / 100);
          any = Math.max(any, v);
        }
        if (any > 0) send({ riffle: 'spectrum', bands });

        if (!settings.level) return;
        // Even out loudness: only ever turns loud tracks down, never boosts quiet ones.
        chain.analyser.getFloatTimeDomainData(wave);
        let total = 0;
        for (const s of wave) total += s * s;
        const rms = Math.sqrt(total / wave.length);
        if (rms < 0.005) return;
        loud = loud ? loud * 0.985 + rms * 0.015 : rms;
        const current = chain.auto.gain.value || 1;
        const raw = loud / current;
        const wanted = Math.min(1, Math.max(0.3, 0.16 / raw));
        chain.auto.gain.setTargetAtTime(wanted, chain.ctx.currentTime, 2);
      }, 66);
    }

    AudioNode.prototype.connect = function (target, ...rest) {
      if (target instanceof AudioDestinationNode) {
        let built = chains.get(this.context);
        if (!built) {
          try {
            built = build(this.context);
            chains.set(this.context, built);
            chain = built;
            apply();
            meter();
          } catch (e) {
            return realConnect.call(this, target, ...rest);
          }
        }
        realConnect.call(this, built.pre);
        return target;
      }
      return realConnect.call(this, target, ...rest);
    };

    window.addEventListener('message', e => {
      if (e.data && e.data.riffle === 'settings') {
        settings = e.data.settings;
        apply();
      }
    });

    send({ riffle: 'tap-ready' });
  })();
}
