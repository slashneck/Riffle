// Runs inside SoundCloud's embed. The player already routes its audio through Web Audio,
// so this slots the equalizer, the loudness stage and the meter in front of the speakers.
// Nothing is recorded or saved, and when the tap can't attach the song still plays untouched.
if (location.hostname === 'w.soundcloud.com') {
  (() => {
    const realConnect = AudioNode.prototype.connect;
    const chains = new WeakMap();
    let settings = { bands: [], boost: 0, level: false, volume: 0 };
    let chain = null;
    let fadeLevel = 1;

    const send = m => window.parent.postMessage(Object.assign({ riffle: 1 }, m), '*');

    function build(ctx) {
      const auto = ctx.createGain();
      const pre = ctx.createGain();
      const filters = [0, 1, 2, 3, 4].map(() => ctx.createBiquadFilter());
      const boost = ctx.createGain();

      // Catches peaks by turning them down for a moment instead of bending the waveform,
      // which is what lets the music get louder without distorting. Settings come from
      // measuring: a slower attack lets drum hits clip, a faster release distorts bass.
      const limiter = ctx.createDynamicsCompressor();
      limiter.threshold.value = -6;
      limiter.knee.value = 10;
      limiter.ratio.value = 16;
      limiter.attack.value = 0.002;
      limiter.release.value = 1;

      // the compressor adds a fixed lift of its own, this takes it back out
      const trim = ctx.createGain();
      trim.gain.value = 0.72;

      const analyser = ctx.createAnalyser();
      analyser.fftSize = 2048;
      analyser.smoothingTimeConstant = 0.72;
      analyser.minDecibels = -85;
      analyser.maxDecibels = -18;

      // the volume comes last, so the slider never changes what the limiter or meter see
      const volume = ctx.createGain();
      volume.gain.value = settings.volume;

      // the crossfade too, and it's what two songs overlapping add up through
      const fade = ctx.createGain();
      fade.gain.value = fadeLevel;

      let node = auto;
      for (const n of [pre, ...filters, boost, limiter, trim, analyser, volume, fade]) { realConnect.call(node, n); node = n; }
      realConnect.call(fade, ctx.destination);
      return { ctx, auto, pre, filters, boost, analyser, volume, fade };
    }

    const db = v => Math.pow(10, v / 20);

    function apply() {
      if (!chain) return;
      const now = chain.ctx.currentTime;
      const gains = settings.bands.map(b => b.gain);
      settings.bands.forEach((band, i) => {
        const f = chain.filters[i];
        if (!f) return;
        f.type = band.type;
        f.frequency.setTargetAtTime(band.freq, now, 0.015);
        f.gain.setTargetAtTime(band.gain, now, 0.015);
        f.Q.setTargetAtTime(band.q, now, 0.015);
      });
      // Only a little is taken back when a band is boosted, so raising the bass really
      // sounds like more bass. The limiter looks after whatever pokes through.
      const peak = Math.max(0, ...gains);
      chain.pre.gain.setTargetAtTime(db(-peak * 0.3), now, 0.015);
      chain.boost.gain.setTargetAtTime(db(settings.boost), now, 0.03);
      chain.volume.gain.setTargetAtTime(settings.volume, now, 0.012);
      if (!settings.level) chain.auto.gain.setTargetAtTime(1, now, 0.3);
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
            send({ riffle: 'attached' });
          } catch (e) {
            return realConnect.call(this, target, ...rest);
          }
        }
        realConnect.call(this, built.auto);
        return target;
      }
      return realConnect.call(this, target, ...rest);
    };

    window.addEventListener('message', e => {
      if (e.data && e.data.riffle === 'settings') {
        settings = e.data.settings;
        apply();
      }
      if (e.data && e.data.riffle === 'fade') {
        fadeLevel = e.data.level;
        if (chain) chain.fade.gain.setTargetAtTime(fadeLevel, chain.ctx.currentTime, 0.015);
      }
    });

    send({ riffle: 'tap-ready' });
  })();
}
