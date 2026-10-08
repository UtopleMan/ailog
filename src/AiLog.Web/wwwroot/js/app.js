// Small helpers the Blazor app calls through JS interop.
window.ailog = {
    scrollToId(id) {
        document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block: "start" });
    },
};
