; TEST ONLY. Run in a NEW empty drawing. 12,000 long polylines, six utility layers.
; Each polyline has straight segments 100 + 5 + 100 metres = 205 metres.
; Each layer contains 2,000 polylines = 410,000 metres; grand total 2,460,000 metres.
(defun c:BOQ_FIXTURE (/ layers layer idx x y offset)
  (if (> (sslength (if (ssget "_X") (ssget "_X") (ssadd))) 0)
    (princ "\nFixture requires an empty drawing; nothing changed.")
    (progn
      (setvar "INSUNITS" 6)
      (setq layers '("WATER-DN200-TEST" "SEWER-DN200-TEST" "GAS-DN200-TEST" "ELECTRIC-DUCT-TEST" "TELECOM-DUCT-TEST" "DRAINAGE-DN400-TEST"))
      (setq offset 0)
      (foreach layer layers
        (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord") '(100 . "AcDbLayerTableRecord") (cons 2 layer) '(70 . 0) (cons 62 (+ 1 offset))))
        (setq idx 0)
        (repeat 2000
          (setq x (* (rem idx 40) 200.0) y (+ (* (fix (/ idx 40)) 50.0) (* offset 7.0)))
          (entmakex (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") (cons 8 layer)
            '(100 . "AcDbPolyline") '(90 . 4) '(70 . 0)
            (cons 10 (list x y)) (cons 10 (list (+ x 100.0) y))
            (cons 10 (list (+ x 100.0) (+ y 5.0))) (cons 10 (list (+ x 200.0) (+ y 5.0)))))
          (setq idx (1+ idx)))
        (setq offset (1+ offset)))
      (command "_.ZOOM" "_E")
      (princ "\nTEST FIXTURE: 12000 polylines; 205m each; 410000m per layer; 2460000m total. Save as a new DWG.")))
  (princ))
