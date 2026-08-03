window.slopFactoryMedia = {
    active: null,
    register: function (element) {
        if (!element || element.dataset.slopFactoryRegistered === "true") return;
        element.dataset.slopFactoryRegistered = "true";
        element.addEventListener("play", () => {
            if (window.slopFactoryMedia.active && window.slopFactoryMedia.active !== element) {
                window.slopFactoryMedia.active.pause();
            }
            window.slopFactoryMedia.active = element;
        });
    },
    setRate: function (element, rate) {
        if (element) element.playbackRate = rate;
    },
    stop: function (element) {
        if (!element) return;
        element.pause();
        element.removeAttribute("src");
        element.load();
        if (window.slopFactoryMedia.active === element) window.slopFactoryMedia.active = null;
    }
};
