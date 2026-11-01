;; Independent read-only verification of the 12,000-polyline stress fixture
;; and its exported DWG. Run BOQ_VERIFY after opening either drawing.
(vl-load-com)
(defun c:BOQ_VERIFY (/ ss i en obj layer len rows old total count file path ok)
  (setq ss (ssget "_X" '((0 . "LWPOLYLINE") (410 . "Model"))))
  (setq i 0 total 0.0 count 0 rows nil)
  (if ss
    (repeat (sslength ss)
      (setq en (ssname ss i) obj (vlax-ename->vla-object en)
            layer (vla-get-Layer obj) len (vla-get-Length obj)
            total (+ total len) count (1+ count) i (1+ i)
            old (assoc layer rows))
      (if old
        (setq rows (subst (list layer (1+ (cadr old)) (+ len (caddr old))) old rows))
        (setq rows (cons (list layer 1 len) rows)))))
  (setq ok (and (= (getvar "INSUNITS") 6) (= (getvar "MEASUREMENT") 1)
                (= count 12000) (= (length rows) 6) (equal total 2460000.0 0.00001)))
  (foreach row rows
    (if (not (and (= (cadr row) 2000) (equal (caddr row) 410000.0 0.00001)))
      (setq ok nil)))
  (setq path (strcat (getvar "DWGPREFIX") (getvar "DWGNAME") ".verification.csv")
        file (open path "w"))
  (write-line (strcat "Verdict," (if ok "PASS" "FAIL")) file)
  (write-line (strcat "INSUNITS," (itoa (getvar "INSUNITS"))) file)
  (write-line (strcat "MEASUREMENT," (itoa (getvar "MEASUREMENT"))) file)
  (write-line "Layer,Count,NativeLength" file)
  (foreach row rows
    (write-line (strcat (car row) "," (itoa (cadr row)) "," (rtos (caddr row) 2 8)) file))
  (write-line (strcat "TOTAL," (itoa count) "," (rtos total 2 8)) file)
  (close file)
  (princ (strcat "\nBOQ_VERIFY " (if ok "PASS" "FAIL") " -> " path))
  (princ))
