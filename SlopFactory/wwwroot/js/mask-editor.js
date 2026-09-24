(() => {
  const sessions = new WeakMap();

  const loadImage = source => new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('The mask image could not be loaded.'));
    image.src = source;
  });

  const waitForImage = image => image.complete
    ? image.naturalWidth > 0 ? Promise.resolve() : Promise.reject(new Error('The asset image could not be loaded.'))
    : new Promise((resolve, reject) => {
      image.addEventListener('load', resolve, { once: true });
      image.addEventListener('error', () => reject(new Error('The asset image could not be loaded.')), { once: true });
    });

  const point = (canvas, event) => {
    const rect = canvas.getBoundingClientRect();
    return {
      x: (event.clientX - rect.left) * canvas.width / rect.width,
      y: (event.clientY - rect.top) * canvas.height / rect.height,
      scale: canvas.width / rect.width
    };
  };

  const stroke = (canvas, event, begin) => {
    const session = sessions.get(canvas);
    if (!session) return;
    const position = point(canvas, event);
    const context = session.context;
    context.globalCompositeOperation = session.tool === 'erase' ? 'destination-out' : 'source-over';
    context.strokeStyle = '#ff436f';
    context.lineWidth = session.brushSize * position.scale;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    context.beginPath();
    if (begin) context.moveTo(position.x - 0.01, position.y);
    else context.moveTo(session.lastX, session.lastY);
    context.lineTo(position.x, position.y);
    context.stroke();
    session.lastX = position.x;
    session.lastY = position.y;
  };

  const bindPointerEvents = canvas => {
    if (canvas.dataset.maskEventsBound === 'true') return;
    canvas.dataset.maskEventsBound = 'true';
    canvas.addEventListener('pointerdown', event => {
      if (event.button !== 0 && event.pointerType !== 'touch') return;
      const session = sessions.get(canvas);
      if (!session) return;
      event.preventDefault();
      canvas.setPointerCapture(event.pointerId);
      session.painting = true;
      stroke(canvas, event, true);
    });
    canvas.addEventListener('pointermove', event => {
      if (!sessions.get(canvas)?.painting) return;
      event.preventDefault();
      stroke(canvas, event, false);
    });
    const stop = event => {
      const session = sessions.get(canvas);
      if (session) session.painting = false;
      if (canvas.hasPointerCapture?.(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    };
    canvas.addEventListener('pointerup', stop);
    canvas.addEventListener('pointercancel', stop);
  };

  window.maskEditor = {
    async attach(canvas, image, maskSource, tool, brushSize) {
      await waitForImage(image);
      if (!image.naturalWidth || !image.naturalHeight) throw new Error('The asset image has no dimensions.');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      context.clearRect(0, 0, canvas.width, canvas.height);
      if (maskSource) {
        const mask = await loadImage(maskSource);
        context.drawImage(mask, 0, 0, canvas.width, canvas.height);
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
        for (let offset = 0; offset < pixels.data.length; offset += 4) {
          pixels.data[offset] = 255;
          pixels.data[offset + 1] = 67;
          pixels.data[offset + 2] = 111;
        }
        context.putImageData(pixels, 0, 0);
      }
      sessions.set(canvas, { context, tool, brushSize, painting: false, lastX: 0, lastY: 0 });
      bindPointerEvents(canvas);
      return [canvas.width, canvas.height];
    },
    setTool(canvas, tool) {
      const session = sessions.get(canvas);
      if (session) session.tool = tool;
    },
    setBrushSize(canvas, brushSize) {
      const session = sessions.get(canvas);
      if (session) session.brushSize = brushSize;
    },
    clear(canvas) {
      const session = sessions.get(canvas);
      if (session) session.context.clearRect(0, 0, canvas.width, canvas.height);
    },
    exportPng(canvas) {
      const session = sessions.get(canvas);
      if (!session) throw new Error('The mask canvas is not ready.');
      const output = document.createElement('canvas');
      output.width = canvas.width;
      output.height = canvas.height;
      const context = output.getContext('2d');
      const pixels = session.context.getImageData(0, 0, canvas.width, canvas.height);
      for (let offset = 0; offset < pixels.data.length; offset += 4) {
        pixels.data[offset] = 255;
        pixels.data[offset + 1] = 255;
        pixels.data[offset + 2] = 255;
      }
      context.putImageData(pixels, 0, 0);
      return output.toDataURL('image/png');
    }
  };
})();
