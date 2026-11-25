// Tema e brilho dos cartões. Carregado no <head>, de forma síncrona, para aplicar o tema antes da
// primeira pintura (sem flash). A preferência fica só no navegador (localStorage), sem cookie nem servidor.
(() => {
    const storageKey = "ouroboros-theme";
    const root = document.documentElement;
    const lightQuery = window.matchMedia("(prefers-color-scheme: light)");

    const stored = () => {
        try {
            const value = window.localStorage.getItem(storageKey);
            return value === "light" || value === "dark" ? value : null;
        } catch {
            return null;
        }
    };

    const apply = (theme) => {
        root.dataset.theme = theme;
        root.style.colorScheme = theme;
        // Cor da barra do navegador no celular acompanha o tema.
        document.querySelector('meta[name="theme-color"]')?.setAttribute("content", theme === "light" ? "#fafafa" : "#09090b");
    };

    apply(stored() ?? (lightQuery.matches ? "light" : "dark"));

    // Sem escolha explícita, acompanha a mudança do sistema.
    lightQuery.addEventListener("change", (event) => {
        if (!stored()) {
            apply(event.matches ? "light" : "dark");
        }
    });

    // Brilho que acompanha o ponteiro nos cartões (só posição; sem animação contínua).
    document.addEventListener("pointermove", (event) => {
        const card = event.target instanceof Element ? event.target.closest(".o-card") : null;
        if (!card) {
            return;
        }

        const bounds = card.getBoundingClientRect();
        card.style.setProperty("--mx", `${event.clientX - bounds.left}px`);
        card.style.setProperty("--my", `${event.clientY - bounds.top}px`);
    }, { passive: true });

    window.ouroboros = {
        theme: {
            get: () => root.dataset.theme,
            set: (theme) => {
                apply(theme);
                try {
                    window.localStorage.setItem(storageKey, theme);
                } catch {
                    // Armazenamento indisponível (janela privada, bloqueio): o tema vale só nesta página.
                }
            },
        },
    };
})();
