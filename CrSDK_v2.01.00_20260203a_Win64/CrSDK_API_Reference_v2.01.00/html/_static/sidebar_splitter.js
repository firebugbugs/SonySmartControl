window.addEventListener('load', onLoad);
function onLoad() {
    var splitters = document.getElementsByClassName("splitter");
    if (splitters.length == 0)
        return;
    var splitter = splitters[0];
    console.log("hello," + splitter);
    // const sidebars = document.getElementsByClassName("wy-nav-side");
    var sidebars = document.getElementsByClassName("wy-side-scroll");
    if (sidebars.length == 0)
        return;
    var sidebar = sidebars[0];
    splitter.addEventListener("mousedown", function (event) {
        document.addEventListener("mousemove", resize, false);
        document.addEventListener("mouseup", function () {
            document.removeEventListener("mousemove", resize, false);
        }, false);
    });
    function resize(e) {
        var size = "".concat(e.x, "px");
        console.log("size:".concat(size));
        sidebar.style.width = size;
    }
}
