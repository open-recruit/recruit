/*
  Hero screenshot carousel on the landing page (overrides/home.html). Advances every few seconds,
  pauses while hovered or focused, and doesn't autoplay for people who prefer reduced motion.
  document$ emits on every page load, including instant navigation, which replaces the hero.
*/
(function () {
  const INTERVAL_MS = 6000;
  let timer;

  function init() {
    clearInterval(timer);

    const carousel = document.querySelector(".rc-carousel");
    if (!carousel) {
      return;
    }

    const slides = carousel.querySelectorAll(".rc-carousel__slide");
    const dotList = carousel.querySelector(".rc-carousel__dots");
    const dots = dotList.querySelectorAll(".rc-carousel__dot");
    let current = 0;

    function show(index) {
      current = (index + slides.length) % slides.length;
      slides.forEach((slide, i) => slide.classList.toggle("is-active", i === current));
      dots.forEach((dot, i) => dot.setAttribute("aria-current", String(i === current)));
    }

    function start() {
      clearInterval(timer);
      if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
        return;
      }
      timer = setInterval(() => {
        if (!carousel.matches(":hover, :focus-within")) {
          show(current + 1);
        }
      }, INTERVAL_MS);
    }

    // restart the timer on a click, so the chosen screenshot gets the full interval
    dots.forEach((dot, i) =>
      dot.addEventListener("click", () => {
        show(i);
        start();
      }),
    );

    dotList.hidden = false;
    start();
  }

  if (window.document$) {
    window.document$.subscribe(init);
  } else {
    document.addEventListener("DOMContentLoaded", init);
  }
})();
