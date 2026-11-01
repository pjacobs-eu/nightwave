// The dialog for a lost connection to the server. Blazor keeps trying by itself and says how it goes
// through components-reconnect-state-changed; this adds the way out when that does not get anywhere.
(() => {
    const dialog = document.getElementById('components-reconnect-modal');

    if (!dialog) {
        return;
    }

    const secondsUntilReload = 30;
    const probeEvery = 3000;
    const note = dialog.querySelector('[data-note]');
    let timer = null;

    const stop = () => {
        clearTimeout(timer);
        timer = null;
    };

    const open = () => {
        if (!dialog.open) {
            dialog.showModal();
        }
    };

    // Reloading while the server is away would leave the browser's own error page, which never
    // comes back by itself. So the page only goes once the server answers.
    const reloadWhenReachable = async () => {
        stop();

        try {
            const answer = await fetch('app.css', { method: 'HEAD', cache: 'no-store' });

            if (answer.ok) {
                location.reload();

                return;
            }
        } catch {
            // Not reachable yet.
        }

        note.textContent = dialog.dataset.waiting;
        timer = setTimeout(reloadWhenReachable, probeEvery);
    };

    const countDown = left => {
        if (left === 0) {
            reloadWhenReachable();

            return;
        }

        note.textContent = dialog.dataset.countdown.replace('{0}', left);
        timer = setTimeout(() => countDown(left - 1), 1000);
    };

    dialog.querySelector('[data-reload]').addEventListener('click', reloadWhenReachable);

    dialog.addEventListener('components-reconnect-state-changed', event => {
        switch (event.detail.state) {
            case 'show':
            case 'retrying':
                open();

                if (timer === null) {
                    countDown(secondsUntilReload);
                }

                break;
            case 'hide':
                stop();
                dialog.close();
                break;
            default:
                // Failed or rejected: this page's session on the server is gone for good.
                open();
                reloadWhenReachable();
                break;
        }
    });
})();
