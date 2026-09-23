document.addEventListener('pointerdown', event => {
  const splitter = event.target.closest('.splitter');
  if (!splitter) return;
  const split = splitter.parentElement;
  const vertical = split.classList.contains('split-vertical');
  const box = split.getBoundingClientRect();
  const move = moveEvent => {
    const available = vertical ? box.height : box.width;
    const offset = vertical ? moveEvent.clientY - box.top : moveEvent.clientX - box.left;
    const percent = Math.max(22, Math.min(78, offset / available * 100));
    split.style.setProperty('--first', `${percent}%`);
  };
  const up = () => {
    document.removeEventListener('pointermove', move);
    document.removeEventListener('pointerup', up);
    splitter.classList.remove('dragging');
    const ratio = parseFloat(split.style.getPropertyValue('--first')) / 100;
    if (Number.isFinite(ratio)) window.recursiveDock?.setSplitRatio?.(split.dataset.splitId, ratio);
  };
  splitter.classList.add('dragging'); document.addEventListener('pointermove', move); document.addEventListener('pointerup', up); event.preventDefault();
});
