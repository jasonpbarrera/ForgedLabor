// Lightweight conversion & interaction tracker for Forged Labor
// Safely forwards events to Google Analytics 4 (gtag) and Microsoft Clarity when configured.
(function () {
    function trackEvent(eventName, params) {
        try {
            var payload = Object.assign({ page_path: window.location.pathname }, params || {});
            if (typeof window.gtag === 'function') {
                window.gtag('event', eventName, payload);
            }
            if (typeof window.clarity === 'function') {
                window.clarity('event', eventName);
            }
        } catch (e) {
            // Never disrupt user interaction if analytics is blocked
        }
    }

    window.forgedTrack = trackEvent;

    document.addEventListener('click', function (ev) {
        var link = ev.target && ev.target.closest ? ev.target.closest('a') : null;
        if (!link) return;

        var href = link.getAttribute('href') || '';
        if (href.indexOf('tel:') === 0) {
            trackEvent('click_call', {
                event_category: 'engagement',
                event_label: href.replace('tel:', '').trim(),
                link_text: (link.textContent || '').trim().slice(0, 60)
            });
        } else if (href.indexOf('/docs/') !== -1 || href.toLowerCase().endsWith('.pdf')) {
            trackEvent('download_capability_sheet', {
                event_category: 'commercial_b2b',
                file_name: href.split('/').pop()
            });
        } else if (href === '/review' || href.indexOf('maps.app.goo.gl') !== -1) {
            trackEvent('click_google_review', {
                event_category: 'reputation',
                event_label: href
            });
        } else if (href === '/appointments') {
            trackEvent('click_book_online', {
                event_category: 'booking',
                link_text: (link.textContent || '').trim().slice(0, 60)
            });
        }
    }, true);
})();
