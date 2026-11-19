// Aviso sonoro opcional de la pantalla de cocina: un pitido corto generado con Web Audio (sin archivos).
let context = null;

export function beep() {
    try {
        context = context || new (window.AudioContext || window.webkitAudioContext)();
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.type = "sine";
        oscillator.frequency.value = 880;
        gain.gain.value = 0.15;
        oscillator.connect(gain);
        gain.connect(context.destination);
        oscillator.start();
        oscillator.stop(context.currentTime + 0.25);
    } catch {
        // Sin audio disponible: el aviso visual sigue funcionando.
    }
}
