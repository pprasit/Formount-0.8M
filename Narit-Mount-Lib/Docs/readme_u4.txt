The fourth U.S. Naval Observatory CCD Astrograph Catalog (UCAC4)

Table of contents

1) Introduction
  a) Overview
  b) Differences between various versions of UCAC and how to get the data

2) Details about observations and reductions
  a) Observations and instrumentation
  b) Reductions of UCAC observations
  c) Data used to derive proper motions
  d) Computation of proper motions
  e) High proper motion (HPM) stars
  f) Photometric data 

3) Properties of the catalog and important notes for the user
  a) Sky coverage
  b) Completeness
  c) Reference frame 
  d) Magnitudes
  e) Additional photometry
  f) Non-stellar data
  g) Provided utility software
  h) UCAC reference material
  i) Arrangement of the data files
  j) Star identification numbers (name)
  k) Pitfalls for programmers / access code development

4) Files contained on the DVD

5) Data formats
  a) Main catalog zone files 
  b) Min/max and check sums
  c) Other files 

6) Description of utility software

7) Acknowledgments

8) References

======================================================================

1) Introduction
---------------

1a) Overview
------------
This is the "readme" file of the fourth U.S. Naval Observatory CCD
Astrograph Catalog, UCAC4.  This data release is available free of
charge on a double sided DVD or through astronomical data centers.

UCAC4 is a compiled, all-sky star catalog covering mainly the 8 to 
16 magnitude range in a single bandpass between V and R.  Positional 
errors are about 15 to 20 mas for stars in the 10 to 14 mag range. 
Proper motions have been derived for most of the about 113 million
stars utilizing about 140 other star catalogs with significant epoch
difference to the UCAC CCD observations.  These data are supplemented
by 2MASS photometric data for about 110 million stars and 5-band
(B,V,g,r,i) photometry from the APASS (AAVSO Photometric All-Sky
Survey) for over 50 million stars. UCAC4 also contains error estimates
and various flags.  All bright stars not observed with the astrograph
have been added to UCAC4 from a set of Hipparcos and Tycho-2 stars.
Thus UCAC4 should be complete from the brightest stars to about R=16,
with the source of data indicated in flags.  UCAC4 also provides a
link to the original Hipparcos star number with additional data such
as parallax found on a separate data file included in this release.

The proper motions of bright stars are based on about 140 catalogs,
including Hipparcos and Tycho, as well as all catalogs used for the 
Tycho-2 proper motion construction.  Proper motions of faint stars are 
based on re-reductions of early epoch SPM data (-90 to about -20 deg 
Dec) and NPM (PMM scans of early epoch blue plates) for the remainder 
of the sky.  These early epoch SPM data have also been combined with
late epoch SPM data to arrive at proper motions partly independent
from UCAC4 (Girard et al. 2011).  The NPM data used in UCAC4 are not
published.  No Schmidt plate data are used in UCAC4.

The unpublished plate measure data obtained by StarScan from the AGK2,
the Hamburg Zone Astrograph, the USNO Black Birch Astrograph, and the 
Lick Astrograph have contributed to considerable improvement in proper 
motions for stars mainly in the 10 to 14 mag range (down to the UCAC 
limit for Lick data); however, these data do not cover all sky.

Recources permitting, USNO plans to release the individual CCD 
observations (RA,Dec at epoch of each CCD observations) in the future.
Please contact nz@usno.navy.mil  if you are interested in obtaining 
this set of about 50 GB data.  We will likely request that interested 
users provide an external disk drive for the data release.


1b) Differences between various versions of UCAC and how to get the data
------------------------------------------------------------------------
UCAC1 = initial results covering small sky area in the south (obsolete)

UCAC2 = -90 to about +50 deg Dec, 48 mill.stars, restricted to good
        astrometry sources, systematic astrometric error corrections
        performed differently than for UCAC4 (mostly obsolete by now)

UCAC3 = first all-sky catalog, new pixel reductions, more elaborate
        systematic error corrections, push for faint limit, not as clean
        as UCAC2 data, several bugs affecting ~ 1 % of stars (obsolete)

UCAC4 = improved version of UCAC3 = final release  

UCAC3 features a number of major differences with respect to UCAC2:
 - complete sky coverage
 - re-reduction of the pixel data with better modeling
 - double stars are resolved to the limit of the data
 - significantly improved photometry from CCD data
 - slightly deeper limiting magnitude with larger number of stars/area
 - reduced systematic errors of CCD observations
 - the addition of several new catalogs for improved proper motions
 - photometry in the B, R, and I bands from the SuperCosmos project
 - minor planet observations have been sorted out
 - identification of more high proper motion stars 
 - match with 2MASS extended sources and LEDA galaxies

UCAC4 is largely based on UCAC3 (same pixel data reductions),
 however, has a number of critical improvements over UCAC3:
 - bug fixes (e.g. missing stars, multiple entries, mag.eq. corrections)
 - use NPM data to derive proper motions of faint stars north of -20 Dec
 - final tweak of systematic error corrections brings it closer to UCAC2
 - photometry in the B, V, g, r and i bands from APASS for 50 mill.stars
 - use APASS photometry to calibrate ("flatten") instrumental mags
 - removal of photometric bias as function of CCD x-coordinate
 - add brightest stars from FK6, Hipparcos and Tycho-2 catalogs
 - link to Hipparcos star numbers and inclusion of Hipparcos 2007 release
  data like parallaxes 
 - cross reference to Tycho-2 star numbers

In August 2011 a pre-release UCAC4-beta catalog was constructed and
distributed to several colleagues worldwide.  The UCAC team appreciates
the very helpful feedback provided by these reviewers. Unfortunately
due to the deployment of the URAT (Zacharias & Gaume 2010) instrument,
completion of the UCAC was delayed further.  A journal paper (Zacharias
et al. 2012) describing the UCAC4 release is in preparation.  
The UCAC4 release paper should be cited whenever UCAC data are utilized.

In July 2012 the UCAC4 data are sent to CDS and it is expected that the 
UCAC4 catalog will become on-line shortly.  The UCAC4 double-sided DVD 
automatically will be sent to all addresses on our distribution list 
(people who received or requested UCAC2 or UCAC3) as fast as our resources
allow.  If you are not on that list or did not receive the DVD say by
October 2012, please send a short e-mail request to brenda.hicks@navy.mil,
put "UCAC4" in the subject line and your complete postal mailing address
in the body of the message (text format).

The UCAC4 comes free of charge.  Technical questions may be addressed to 
nz@usno.navy.mil (Norbert Zacharias) or finch@usno.navy.mil (Charlie Finch).
The latest update on astrometry related projects at USNO can be found at:
www.usno.navy.mil/usno/astrometry . 


2) Details about observations and reductions
--------------------------------------------

2a) Observations and Instrumentation
------------------------------------
The UCAC is an observational program, using the U.S. Naval Observatory
Twin Astrograph and a 4k by 4k CCD camera, covering just over one square
degree per frame with a scale of 0.9"/pixel.  The red-corrected, 20 cm
aperture, 5-element lens of the astrograph provides a 9 degree diameter
field of view (designed for photographic plates), thus only a fraction is
utilized with our CCD camera, centered on the optical axis. The same lens
in a new tube assembly is now used with a 476 million pixel LN2 cooled
camera for the URAT program (Zacharias 2004, Zacharias & Gaume 2010).

The 4k CCD is a thick, Kodak device with 9 micrometer square pixels.
The camera, made by Spectral Instruments, is Peltier cooled to -18 C.
The raw data are severely affected by a low charge transfer efficiency
(CTE) of our otherwise cosmetically excellent CCD chip.  To mitigate 
this problem, a relatively warm operating temperature is used, causing 
a significant dark current.

Observations started in January 1998 at Cerro Tololo Interamerican
Observatory (CTIO) in Chile, where the entire southern sky and about 
half of the northern sky were observed.  In October 2001 the instrument
was moved to the Naval Observatory Flagstaff Station (NOFS) in Arizona
where it completed the northern sky in 2004.  

A 2-fold, center-in-corner overlap pattern was adopted on a 0.5 degree
grid, starting at the South Celestial Pole.  Each field was observed
with both a long (100 to 150 sec) and a short (20 to 30 sec) exposure.
Extensive quality control routines led to the rejection of over 15% of
the frames taken.  All raw (pixel) data were saved and archived.

Observations were made in a single bandpass (579-642 nm), thus the UCAC
magnitudes are between Johnson V and R.  No attempt has been made to
obtain high quality, photometric data from the CCD observations.  In
fact, observations were made during nights with thin cirrus clouds.

The telescope was actively guided with an ST-4 autoguider mounted behind
the second, visually-corrected lens of the twin-astrograph.  Operation
was automated using a PC and a single board computer.  An HP-Unix
workstation was used for on-line reductions to obtain quality control
statistics in near real-time.


2b) Reductions of UCAC Observations
-----------------------------------
The UCAC4 positions of the CCD observations are based on the Tycho-2
reference stars (Hoeg et al. 2000), similar to the UCAC2 catalog
(Zacharias et al. 2004).  However, the 2MASS was utilized to probe 
for systematic errors in the CCD data, particularly magnitude equations
and coma-like errors caused by the poor CTE of the detector (Finch, 
Zacharias & Wycoff 2010).  Image centering is based on a modified
Lorentz profile model which matches the observed PSF better than a
Gaussian function (Zacharias 2010).

For UCAC4 the pixel reduction results of UCAC3 was adopted with the
only change being the identification and elimination of close multiple
images on the same CCD frame.
Pixel processing included double star fits and real aperture photometry.
"Flip" observations (telescope on West and East side of pier for the same
calibration fields) were utilized to fix the overall magnitude equations.

Saturated images of stars were propagated through the pipeline into the
output catalog.  Thus similar to UCAC3, UCAC4 contains many more bright 
stars than UCAC2.  However positional results are not as reliable as for 
unsaturated images and the user is urged to pay attention to flags and 
the meaning of various data columns.  

To improve this situation, a new feature was introduced with UCAC4:
For stars brighter than UCAC model or aperture magnitude 8.5 and if
no "good" image from UCAC CCD is available or the object is flagged
as blended image or the position difference to Tycho-2 is larger than
50 mas in either coordinate, the FK6/Hipparcos/Tycho-2 astrometric data
are used instead of the UCAC observational data.  Furthermore, UCAC4
was supplemented by FK6/Hipparcos/Tycho-2 data for all bright stars not
observed with the UCAC astrograph.  The source of data is indicated by
a flag in UCAC4.  Nevertheless the purpose of those supplement stars is
to make the user aware of these bright stars.  No claim is made to have
the best astrometric and photometric data available for those stars, or
even have the identification right in all cases in UCAC4.  Users of bright
stars ought to look at a variety of catalogs to compare all information 
available.

As before, some positions (can happen to stars of all magnitudes) are
based on a center-of-mass centroiding when the least-squares fit did 
not succeed.  Those observations are identified by number of used
images (nu) equal to 0.  The positions of those stars should be used 
with caution.


2c) Data used to derive Proper Motions
--------------------------------------
A master list of exactly 181,895,143 mean positions from CCD data was
matched against the various other catalogs.  This number is larger than
before due to a lower threshold used to accept individual images from
CCD pixel data reductions.

Proper motions of bright stars (R ~8 to ~12.5) were derived using a
combination of ground-based photographic and transit circle catalogs,
and included satellite observations from the Hipparcos and Tycho-2 
catalogs.  In addition, the U.S. Naval Observatory measured about 
5000 astrograph plates on the StarScan machine to derive about 9 million 
positions for stars mainly in the 10 to 14 magnitude range, including 
the complete set of AGK2 (Bonn and Hamburg zones, +90 to -2.5 deg Dec),
as well as about 30 % of the sky covered by the USNO Black Birch (south)
and Hamburg Zone astrograph (north) programs.

For the faint stars (~12.5 to ~16.5), data from the first epoch plates
of the Yale Southern Proper Motions (SPM, van Altena et al. 1999) are 
utilized.  These plates were measured on the Precision Measuring Machine
(PMM) at USNO Flagstaff Station by D. Monet.  A complete re-reduction of 
the data was performed in a joint USNO - Yale University effort utilizing
the StarScan pipeline for the pixel reductions and the Yale software to 
obtain RA,Dec coordinates (Girard et al. 2011). The SPM data used for UCAC4
are based on the early epoch (about 1970) blue and visual plates covering
the about -90 to -20 deg declination sky area.  For the rest of the sky
an unpublished star catalog (Girard, private comm.) based on NPM 1st
epoch (about 1950) blue plates was used.  These plates were also measured
on the PMM and processed with the StarScan and Yale Univ. pipelines.
Only for the SPM data original pixel data of plate scans were available.
The NPM data shows larger systematic errors than the SPM data. However,
the epoch difference to UCAC is larger for NPM than for SPM resulting in
about an equal amount of estimated, remaining systematic errors of a few
mas/yr for UCAC4 proper motions based on those data.  

When deriving UCAC4 proper motions from all individual epoch positions,
estimates of systematic errors for each catalog entered the weights.
The number of stars in UCAC4 which are also in major other catalogs 
used for the proper motions are as follows:

 numb.stars  catalog 
     120487  Hipparcos  (including bright supplement stars)
    2506683  Tycho-2    (including bright supplement stars)
    4373790  AC2000
     279570  AGK2 Bonn
     982815  AGK2 Hamb
    4682287  Zone Astrograph
    3492601  Black Birch Astrograpph
    1104138  Lick astrograph selected fields
   68887550  NPM Lck1
   57355612  SPM YSJ1


2d) Computation of Proper Motions
---------------------------------
The computation of proper motions is performed similarly to the procedure
used for the UCAC2 and Tycho-2 catalogs.  All input catalogs were
reduced to the ICRF utilizing Hipparcos data or some denser, interim
catalog that follows the system of Hipparcos.  Standard errors for each
position are estimated. These error estimates and RMS added, estimated
systematic errors are used as weights to compute a mean position and 
proper motion by a weighted, least-squares adjustment procedure.  
Estimates of errors for UCAC4 positions and proper motions are provided.

Note, while calculating proper motions, no attempt was made to correct
data for parallaxes.  This will lead to slightly inferior results for
few stars with high parallax if it involves observations from largely
different parallax factors.

Errors in proper motions of the bright stars (to R ~12) run from about
1 to 3 mas/yr benefited by the large epoch spans involved.  For the
fainter stars using SPM and NPM data, typical errors are 2 to 6 mas/yr. 

Contrary to UCAC2, and similar to UCAC3, not all stars in UCAC4 have 
proper motions.  Stars observed by the astrograph made it into the UCAC4
release catalog if any one of the following applies:
 - at least 2 matching positions from different CCD frames were obtained,
 - star was matched with a 2MASS entry,
 - proper motions could be obtained in combination with any other
  early epoch catalog.

The "plots" folder of the UCAC4 distribution DVD contains all-sky plots
with color coded proper motions for the RA*cosDec and Dec component,
respectively.  Mean proper motions for stars in 0.5 by 0.5 deg boxes
are shown after cutting the low/high 15% of proper motions in each box.

For more details please see also the next section and the upcoming UCAC4
journal paper, as well as the published UCAC2 and UCAC3 journal papers
(also found on the UCAC4 distribution DVD).


2e) High proper motion (HPM) stars
----------------------------------
Note 1: this includes also stars with not so large a proper motion if 
they are present in the below mentioned external surveys and catalogs
aiming at discovering high proper motion stars.  

Note 2: automatic processing of high proper motion stars is prone to
mismatches with early epoch data and blended images cause issues.
Likely there will be some false data in UCAC4 for hight proper motion
astrometry.  In particular, we do not claim completeness of UCAC4
data with respect to high proper motion stars.  Most stars with a
proper motion of about 200 mas/yr or smaller should be included in UCAC4,
as well as a hand-picked sample of top high proper motion stars. However,
coverage of the range in between could be significantly incomplete.

Stars with high proper motions were handled specifically.  First a
catalog of 1.8 million stars was constructed from published proper
motions.  In the North the LSPM-North Catalog (Lepine & Shara 2005)
of 61977 new and previously known high proper motion stars having
proper motions greater than 0.15"/yr was used. In the South many smaller
surveys along with the Revised NLTT Catalog (Salim & Gould 2003) were
used, which produced 17730 unique high proper motion stars greater than
0.15"/yr. In both the North and South a supplement list of proper
motion stars greater than ~0.15"/yr from the Tycho-2 and Hipparcos
catalogs were used to fill in any gaps. In chronological order, the
smaller southern surveys used include: (1) 7 papers covering various
portions of the southern sky by Wroblewski and collaborators
(Wroblewski & Torres 1989, 1991, 1994, 1996, 1997; Wroblewski & Costa
1999, 2001), (2) UK Schmidt Telescope survey plates of 40 survey
fields by Scholz and collaborators (Scholz et al. 2000), (3) The
Calan-ESO survey (Ruiz et al. 2001) (4) SuperCOSMOS-RECONS
proper-motion survey of the entire southern sky (Henry et al. 2004;
Subasavage et al. 2005a, 2005b; Finch et al. 2007; Boyd et al. 2011),
(5) the Southern Infrared Proper-Motion Survey (SIPS; Deacon et
al. 2005), (7) Lepine's SUPERBLINK survey of a portion of the southern
sky (Lepine 2008) and (8) UCAC3 proper motion survey (Finch et
al. 2010, 2012).)

Then we identified these stars in our CCD observations in a 2-step 
approach.  For each individual exposure we establish a list of HPM stars 
which could be present in that field.  HPM star positions were calculated
for the epoch of that exposure and then matched with the individual RA,Dec
observations of that exposure to identify and flag HPM stars on each
exposure (object type = 3).

Contrary to UCAC3, this time a UCAC4 based solution for mean position 
and proper motion was attempted for all stars including the HPM stars. 
The results were analyzed as follows:

The position and proper motion solution obtained by the above procedure
was substituted by zero proper motion and the mean CCD data position at
mean observational epoch for the following cases:
 - PM solution failed or shows large errors (>= 500 mas, >= 50 mas/yr)
 - derived PM is larger than 500 mas/yr in either component
 - derived central epoch is earlier than 1947
 - RA or Dec position difference to CCD data mean position is >= 3 arcsec 

These stars are thus added to the group of "no proper motion" stars,
i.e. those which did not match up with other catalogs to even begin the
proper motion calculation.  All stars where then checked against the 
external set of HPM stars.  The PM from the external catalog was used for
stars with no UCAC4 PM solution and for those where the difference in PM 
for either component exceeded 40 mas/yr.  Thus we trust the external 
catalog data more than the UCAC4 derived proper motions in those cases.

Comparions of UCAC4 based astrometric solutions of stars with about
100 mas/yr or more with external data (2MASS, thanks Rae Stiening) indicated
unrealistic proper motions for many such stars in UCAC4 data.  Plots of
distribution of number of stars as function of UCAC4 proper motion bins
showed an overdensity around 100 and 200 mas/yr which could be traced back
to blended images or wrong idendifications of stars with NPM and SPM early
epoch data in crowded fields.  NPM and SPM plates were exposed twice
(long and short) with offset between exposures and using an objective 
grating, enhancing the chances of confusion in crowded fields.  In order
to mitigate this problem in UCAC4 new object flags were introduced as
follows.  All stars with object type = 0,1,2 (i.e. those with UCAC4 based 
solution for proper motions, excluding above described special high proper 
motion stars handling, Hipparcos and Tycho data, and supplemented stars) 
and a proper motion larger than 80 mas/yr in either coordinate were picked 
from the UCAC4.  This is a total of about 2.8 million stars.  These were
matched with the PPMXL (Roeser et al 2010) using a match radius of 4 arcsec
with both catalog positions at epoch 2000.  The UCAC4 stars not matched
with PPMXL in this way were assigned the new object type = 8 (2029306 stars).
For the matched stars the proper motion difference vector lenght was 
calcualted and a threshold set to flag discrepant proper motions with 
object type = 9.  The threshold was set as the lower of 80 mas/yr and
(3 times the combined formal errors + 5 mas/yr systematic error floor).
This way 689470 stars in UCAC4 obtained the ojt = 9 flag, while 140887
stars were found to have consistent proper motions from this set of stars
and no change to UCAC4 data flags were made.

Obviously stars flagged with ojt = 8 or 9 should be handled with caution.
Many of the ojt = 8 objects can be bogus stars and many of the ojt = 9
stars are likely affected by blended images.  Unless external data are
consulted to verify UCAC4 results these stars should not be used.


2f) Photometric data
--------------------
For each detected image 2 instrumental magnitudes were derived.
First, the model magnitude is based on the flux volume of the best-fit
image profile model which is also used for the center position result.
Second, an aperture photometry magnitude is calculated.

In addition to the systematic error corrections applied for UCAC3
(Finch et al. 2010), for UCAC4 a bias correction was applied as a
function of the pixel x-coordinate of the image.  This bias is caused
by the poor charge-transfer efficiency (CTE) of the CCD used for UCAC
observations, resulting in image elongation as a function of x.
Different bias models were derived for the model and aperture photometry.

The APASS 5-band photometry of over 9 million stars (DR2) was utilized
to "flatten" the UCAC instrumental magnitudes.  The raw instrumental 
magnitudes are not linear with offsets up to about 0.5 and 0.3 mag at 
the bright and faint end, respectively.  From color-color plots a linear
model was adopted to predict UCAC bandpass magnitudes from APASS r and V.
The predicted minus observed (model and aperture mags handled separately)
UCAC magnitudes were then plotted as fucntion of magnitude and inter-
polation polynomials derived.  These corrections were then applied to 
all UCAC instrumental magnitudes.

All corrected, instrumental magnitudes were then transformed into the 
system of Tycho-2 by a simple zero-point correction (Finch et al. 2010),
the same way as with UCAC3 data.  Photometric results were averaged for
stars with multiple CCD observations to arrive at the UCAC4 catalog
entries.  Nights with poor photometric quality were flagged and
not used in the average, unless no other data are available.

Note, that "poor photometric" quality for UCAC4 means "really bad"
nights, with significant transparency variations due to clouds.
The goal was to obtain approximate magnitudes, maybe reliable on
the 10% level with differential magnitudes good to about 5%.
UCAC is *not* a photometric catalog.

The UCAC4 data are supplemented by 2MASS near-IR magnitudes and
APASS 5-band optical photometry up to its data release DR6 (June 2012). 


3) Properties of the catalog and important notes for the user
-------------------------------------------------------------

3a) Sky coverage
----------------
UCAC4 is an all-sky catalog with at least about 40 stars per square 
deg anywhere on the sky.  The average density of this catalog is over
2000 stars per square deg.  There is a color-coded all-sky plot called
"ucac4all2.pdf" in the "plots" folder showing the logarithm of the number
of stars per box of 0.5 by 0.5 deg on the sky.  Blue is low density, red 
high with green and yellow in between. 


3b) Completeness
----------------
UCAC4 like UCAC3 is more complete than UCAC2, including previously 
omitted "problem" stars and double stars, many of which could be new 
discoveries.  A paper is in preparation about a sample of new double 
stars found in UCAC4 and the rate of confirmation by speckle observations. 
For preliminary results see (Hartkopf et al. 2010). The separation limit 
for double stars in UCAC4 varies as function of brightness of the 
components and brightness difference.  However, some doubles with 
separations of under 2 arcsec in UCAC3 have been confirmed as real.  
Those cases are rare and objects within 2 arcsec of each other have 
generally been merged to at least a blended image in UCAC4.  Some 12,000
sources with a separation of less than 2 arcsec remain in UCAC4, of which
about half entered through the Hipparcos/Tycho-2 supplement data, which 
includes double star annex catalogs.  Stars fainter than R = 10 with 
separations larger than 2 arcsec are likely real double stars in UCAC4.

UCAC4 also contains observations of some bright stars, as they happened
to make it through the pipeline.  However, poor data have been substituted
by FK6, Hipparcos and Tycho-2 data (in that order of available data).
For reduction details on this see section 2b above.  Thus UCAC4 should 
be complete from the brightest naked eye stars to about 16th mag.

The following table gives some general statistics about UCAC4 stars:
  113780093  total number of stars in UCAC4 (incl. supplement stars)
  109921682  with 2MASS identification
  106689821  with proper motions
   81897551  with 2 epoch PM 
   27245403  with 3 or more epoch PM 
   80806744  with 2 or more images from "good fit" CCD observations
   48323349  matched with UCAC2
      54690  matched with LEDA galaxies
      76020  matched with 2MASS extended source catalog
       8925  supplemented stars (no CCD obs.)
     121350  UCAC4 entries with a matched Hipparcos star ID
     104681  UCAC4 entries with CCD obs. substituted by FK6/Hip/Tycho-2

If the computed position error of a star exceeds 500 mas in either
coordinate it was set to 500 mas but the star was kept, if at least
2 observations from different CCD observations were matched or the
star is either in the 2MASS, SPM or NPM data files.
Similarly, the error in proper motion was truncated to 50 mas/yr
but respective stars kept in UCAC4 by the same criteria as for large
positional errors.  Obviously all large error objects need to be
handled with caution, and some of these are simply nonexistent.

This approach, different to UCAC2, was taken in order to drive up the
completeness of UCAC3 and UCAC4 at the expense of slightly greater 
contamination.  For UCAC4 also a lower threshold than for UCAC3 
was adopted in image size for failed image profile fit objects.
In particular, the larger limits in positional errors deemed acceptable
for UCAC3 and UCAC4 accommodate the fainter limiting magnitude, which 
was possible mainly due to superior handling of dark subtraction in the 
pixel data.  This allowed many low signal-to-noise, real stars to enter 
the catalog, although with expected large random errors, as compared to
UCAC2.  


3c) Reference frame
-------------------
The astrometry provided in UCAC4 is on the Hipparcos system, i.e.
the International Celestial Reference System (ICRS), as represented
by the Tycho-2 catalog.  Contrary to earlier UCAC releases, the UCAC4
processing included a down-weighting of Tycho-2 stars with V-magnitude
fainter than 11.5 by a factor of 1.5 w.r.t. their nominal weight.
Any possible deviations of the Tycho system with respect to Hipparcos 
or the extragalactic radio reference frame are still under investigation.  

Positions in UCAC4 are given at the standard epoch of Julian date 2000.0,
thus the UCAC4 is a compiled catalog.  In order to be able to calculate 
positional errors at any epoch, the central epoch, i.e. the weighted mean 
epoch of the data (UCAC + early epoch other catalogs) is given.  At the 
central epoch (which varies from star to star and is also different for 
RA and Dec) the positional error has its smallest value; the one given 
in the catalog for "sigma position".  In most cases this central epoch 
will be close to the UCAC observational epoch due to the relatively large 
weight given to the UCAC observations.  However, a fair number of stars 
have a vastly different mean epoch, ranging back to about 1947.  Proper
motion solutions with central epoch earlier than that were defined as
invalid and substituted by other catalog results if available, or only
the observed CCD position is reported with no proper motion.

The proper motions are given at the central epoch.  Positional errors
of stars increase according to the errors in the proper motions when
going forward or backward in time from the central epoch.


3d) Magnitudes
--------------
UCAC4 observational data (CCD exposures) covers the magnitude range of 
about R = 8 to 16.3 in a 579-642 nm bandpass.  However, data from
FK6, Hipparcos, and Tycho-2 were used to supplement UCAC4 to create
a star catalog complete from brightest stars to about 16th magnitude.
The UCAC bandpass is between visual (V) and red (R).  The limiting 
magnitude can vary by about +-0.3 mag from field to field.  

UCAC4 gives center fit-model magnitudes as well as aperture photometry
derived from the same pixel data reductions already performed for UCAC3.
Systematic errors in these magnitudes are believed to be below 0.1 mag, 
which is a significant improvement over the UCAC2 release.  Non-linearity
of the instrumental magnitudes were calibrated out using APASS photometry
for UCAC4.  Tycho-2 stars (excluding the faint end) were used to determine
the zero-point of the corrected, instrumental magnitudes on a frame by
frame basis.  However, UCAC observations often were performed in non-
photometric sky conditions.  Mean magnitudes were derived from CCD frames
with indications of "acceptable photometric quality", which is about 50%
of the observations.


3e) Additional photometry
-------------------------
The UCAC4 observational data are supplemented with 5-band photometry (B,V,
g,r,i) from the APASS project (Henden, private comm.) as well as with IR 
photometry (J,H,K_s) from the Two Micron All Sky Survey, 2MASS (Skrutskie
et al. 2006).  In addition, magnitudes errors and some flags are provided.
For more details see http://www.aavso.org/apass  and
http://www.ipac.caltech.edu/2mass/releases/allsky/ .


3f) Non-stellar data
--------------------
The UCAC4 contains some galaxies, particularly at the faint end.
No flag indicating a galaxy or star has been derived from the CCD
pixel data; however, a cross reference to the LEDA galaxies (Paturel
et al. 2005) and the 2MASS extended source catalog (Skrutskie et al.
2006) has been made and corresponding information is put into the 
UCAC4 release.  The procedure is the same as for UCAC3.

Observations of asteroids have been identified and taken out of the
main UCAC4 catalog.  Individual epoch observations of about 1000
asteroids at multiple epochs have been prepared for publication, but 
are not part of the UCAC4 release. The observing schedule explicitly 
avoided all minor planets brighter than about R = 12, except for 
special observing campaigns to derive masses of asteroids.


3g) Provided utility software
-----------------------------
Only some basic access code is included with this release to 
illustrate the data format and allow conversion of the main
binary files to ASCII tables.  It is anticipated that 3rd party
companies specialized in providing interfaces between various
star catalogs and the user will integrate UCAC4 data into their
products, as has been the case for UCAC2 and UCAC3.


3h) UCAC reference material
---------------------------
The UCAC4 release on DVD also contains a folder with historical
data.  A copy of the UCAC internet web pages as of January 2012 
is provided as well as a complete collection of papers and 
presentations given about UCAC.  Important other papers about 
data utilized for the UCAC project are given as well.


3i) Arrangement of the data files
---------------------------------
The main catalog data are arranged in declination zones of 0.2 degree 
width, with zone number 1 starting at the South Pole and increasing  
toward north.  There is a separate file for each of the 900 zones.
Records in the individual data files are sorted by right ascension. 
Each record contains the data for one star, 78 bytes of binary integers
as described below.  As with previous releases, an index file is 
provided for fast access.  The step size along RA for that index file
has been significantly reduced to 0.25 degree = 1 minute.  These zone
data files are split up into 2 directories, south (zones 1 to 379) and
north (zones 380 to 900) to fit on the 2-sided DVD.  It is recommended
that users copy the individual zone files into a single directory on
their hard disk drive to simplify all-sky access.


3j) Star identification numbers (name)
--------------------------------------
Official UCAC4 star ID numbers should be utilized for identification
purposes and for communication with the UCAC team:  the UCAC4 star
number is of the following format:

 UCAC4-zzz-nnnnnn

where zzz is the 3 digit zone number (form 001 to 900) and nnnnnn the 
the 6-digit running record number along the zone file.  The cross-
identification to UCAC2 stars follows the same pattern with zone and 
record number of the UCAC2 release given in the UCAC4 data.  

Note, zones numbers in UCAC4 run from 1 to 900 (0.2 deg wide), while 
zone numbers in UCAC2 run from 1 to 360 (0.5 deg wide), in both cases 
beginning at the South Celestial Pole (i.e. in order of declination).


3k) Pitfalls for programmers / access code development
------------------------------------------------------
1. To retrieve the correct proper motions, there is a need to occasionally
 extract data from the extra u4hpm.dat file, see note 8 section 5a.

2. All fields stored as byte are stored as a signed byte. Some of these 
 fields need to have 128 added to the byte value to get the correct value.
 See notes 6 and 9 in section 5a below.

3. For stars with errors in proper motion larger than 25.0 mas/yr the
 error is coded in groups which are then labled as 251,...,254 to fit
 into 1-byte values.  See note 9 in section 5a below.

4. The hipsupl.dat file contains 333 Hipparcos stars that are associated 
 with two (or occasionally three) UCAC4 stars. In such instances, the 
 hipsupl.dat file contains identical Hipparcos data for each UCAC4 star 
 associated with the Hip number.


4) Files contained on the DVD
-----------------------------
The double-sided DVD contains this "readme_u4" file and the following 
directories:

access   = example access code (Fortran and C), and sample output
html     = snapshot of web pages of UCAC project, history, pictures
papers   = some published papers relevant to UCAC, prev.release readme, history
plots    = all-sky plots (as described in sections 2 and 3 above)
u4b      = binary data files (northern and southern part on 2 sides of DVD)
u4i      = index files, supplemental files needed for access code

The split between northern and southern part is not even because there
are many more stars on the southern than northern hemisphere.  Side 1 of
the DVD contains zones z380 to z900 in the folder u4b, while the other 
side of the DVD contains zones z001 to z379 in folder u4b.
If you want to operate UCAC4 from hard disk, copy all the zone files
(directory u4b from both sides) and the u4i folder to your hard disk.

The main catalog data are contained in binary zone files "z001" to "z900",
sorted by declination.  Each zone is 0.2 degree wide, beginning with z001
at the south celestial pole.  Stars are sorted by RA inside a zone file.
The format of these binary data files is explained below.

The u4i folder contains the following files:

hipsupl.dat = additional data from the Hipparcos 2007 release
u4hpm.dat   = ASCII table for high proper motion stars (see below)
u4index.asc = ASCII table index file for each box in RA and each zone
u4index.unf = same table unformatted, binary, used by u4access.f
u4supl.dat  = additional data for supplement and Hipparcos stars
u4xtycho    = ASCII cross reference to Tycho-2 star numbers
zone_stats  = ASCII table, summary of zone files

The "access" folder contains some Fortran and C code and example output 
files.  For a description see section 6) below.  The "plots" folder 
contains all-sky color plots for number of stars and mean proper motion as
function of RA and Dec.  The "papers" folder contains important published
papers relevant to UCAC and an attempt to capture all papers written by
USNO staff about UCAC, as a historic record.  There is also a file about
the USNO Twin astrograph history, the telescope used for the UCAC project. 
The "html" folder contains a snapshot of the UCAC web pages as of January  
2012 from the following link, which may or may not be available in the 
future: www.usno.navy.mil/usno/astrometry/optical-IR-prod/ucac
Open the html/ucac.html file on the DVD with your browser to get started.


5) Data formats
---------------
Binary files in the UCAC4 distribution use the PC-style sequence of bytes.
The same byte sequence is also used on recent Mac computers. Some computers
(UNIX style) need a flip of bytes for 2 and 4 byte integers. An option for
this is build into the access code provided here.

5a) Main catalog zone data files
--------------------------------
The data of the first and last zone are also given in ASCII tables
(access/z001.asc, z900.asc)  to illustrate the format and allow
verification of binary file access by the user.  That table was generated
with the  access/u4dump.f  code.  Columns are separated by commas on
z001.asc, while the z900.asc shows the data of the last zone in ASCII
with blanks separating the columns.

The following shows sample code (Fortran 77) to illustrate the data format,
followed by a table describing each data item (column, same order also on
the ASCII sample output).  After extending some merged flag data there are
a total of 53 columns in the UCAC4 zone data files.  

* items to read from input files
      INTEGER*4 ra,spd, pts_key,rnm, mcf, rnz
      INTEGER*2 magm,maga, cepra,cepdc, pmra2,pmdc2
     .         ,j_m, h_m, k_m,  apasm(5), zn2
      INTEGER*1 sigmag, sigra,sigdc, sigpmr,sigpmd
      INTEGER*1 objt,cdf, na1,nu1,cu1, apase(5), gcflg
      INTEGER*1 icqflg(3), q2mflg(3), leda,x2m

      CHARACTER string*9
      INTEGER   icf(9) 

      OPEN (20,FILE=fnu4,ACCESS='direct',RECL=78) ! 1 record per star
      i = 0
      DO
        i = i + 1
        READ (20,REC=i,ERR=99)                    ! numb. of bytes 78
     .     ra,spd, magm,maga, sigmag,objt,cdf     !  8 + 4 +  3  = 15
     .    ,sigra,sigdc, na1,nu1,cu1               !  2 + 3       =  5
     .    ,cepra,cepdc, pmra2,pmdc2,sigpmr,sigpmd !  4 + 4 +  2  = 10
     .    ,pts_key, j_m,h_m,k_m, icqflg, q2mflg   !  4 + 6 +  6  = 16
     .    ,apasm, apase, gcflg                    ! 10 + 5 +  1  = 16
     .    ,mcf, leda,x2m, rnm                     !  4 + 2 +  4  = 10
     .    ,zn2, rnz                               !  2 + 4       =  6

        WRITE (str,'(i9.9)') mcf    ! merged catalog flag from 4 byte Int to
        READ  (str,'(9i1)')  icf    ! 9 individual catalog flags
      ENDDO
 99   WRITE (*,'(a,i9)') 'last record number in file = ',i-1


col byte item   fmt unit       explanation                            notes
---------------------------------------------------------------------------
 1  1- 3 ra     I*4 mas        right ascension at  epoch J2000.0 (ICRS) (1)
 2  5- 8 spd    I*4 mas        south pole distance epoch J2000.0 (ICRS) (1)
 3  9-10 magm   I*2 millimag   UCAC fit model magnitude                 (2)
 4 11-12 maga   I*2 millimag   UCAC aperture  magnitude                 (2)
 5 13    sigmag I*1 1/100 mag  error of UCAC magnitude                  (3)
 6 14    objt   I*1            object type                              (4)
 7 15    cdf    I*1            combined double star flag                (5)
         15 bytes
 8 16    sigra  I*1 mas        s.e. at central epoch in RA (*cos Dec)   (6)
 9 17    sigdc  I*1 mas        s.e. at central epoch in Dec             (6)
10 18    na1    I*1            total # of CCD images of this star
11 19    nu1    I*1            # of CCD images used for this star       (7)
12 20    cu1    I*1            # catalogs (epochs) used for proper motions
          5 bytes
13 21-22 cepra  I*2 0.01 yr    central epoch for mean RA, minus 1900
14 23-24 cepdc  I*2 0.01 yr    central epoch for mean Dec,minus 1900
15 25-26 pmrac  I*2 0.1 mas/yr proper motion in RA*cos(Dec)             (8)
16 27-28 pmdc   I*2 0.1 mas/yr proper motion in Dec
17 29    sigpmr I*1 0.1 mas/yr s.e. of pmRA * cos Dec                   (9)
18 30    sigpmd I*1 0.1 mas/yr s.e. of pmDec                            (9)
         10 bytes
19 31-34 pts_key I*4           2MASS unique star identifier            (10)
20 35-36 j_m    I*2 millimag   2MASS J  magnitude
21 37-38 h_m    I*2 millimag   2MASS H  magnitude
22 39-40 k_m    I*2 millimag   2MASS K_s magnitude
23 41    icqflg I*1            2MASS cc_flg*10 + ph_qual flag for J    (11)
24 42     (2)   I*1            2MASS cc_flg*10 + ph_qual flag for H    (11)
25 43     (3)   I*1            2MASS cc_flg*10 + ph_qual flag for K_s  (11)
26 44    e2mpho I*1 1/100 mag  error 2MASS J   magnitude               (12)
27 45     (2)   I*1 1/100 mag  error 2MASS H   magnitude               (12)
28 46     (3)   I*1 1/100 mag  error 2MASS K_s magnitude               (12)
         16 bytes
29 47-48 apasm  I*2 millimag   B magnitude from APASS                  (13)
30 49-50  (2)   I*2 millimag   V magnitude from APASS                  (13)
31 51-52  (3)   I*2 millimag   g magnitude from APASS                  (13)
32 53-54  (4)   I*2 millimag   r magnitude from APASS                  (13)
33 55-56  (5)   I*2 millimag   i magnitude from APASS                  (13)
34 57    apase  I*1 1/100 mag  error of B magnitude from APASS         (14)
35 58     (2)   I*1 1/100 mag  error of V magnitude from APASS         (14)
36 59     (3)   I*1 1/100 mag  error of g magnitude from APASS         (14)
37 60     (4)   I*1 1/100 mag  error of r magnitude from APASS         (14)
38 61     (5)   I*1 1/100 mag  error of i magnitude from APASS         (14)
39 62    gcflg  I*1            Yale SPM g-flag*10  c-flag              (15)
         16 bytes
40 63-66 icf(1) I*4            FK6-Hipparcos-Tycho source flag         (16)
41       icf(2) ..             AC2000       catalog match flag         (17)
42       icf(3) ..             AGK2 Bonn    catalog match flag         (17)
43       icf(4) ..             AKG2 Hamburg catalog match flag         (17)
44       icf(5) ..             Zone Astrog. catalog match flag         (17)
45       icf(6) ..             Black Birch  catalog match flag         (17)
46       icf(7) ..             Lick Astrog. catalog match flag         (17)
47       icf(8) ..             NPM  Lick    catalog match flag         (17)
48       icf(9) ..             SPM  YSJ1    catalog match flag         (17)
          4 bytes
49 67    leda   I*1            LEDA galaxy match flag                  (18)
50 68    x2m    I*1            2MASS extend.source flag                (19)
51 69-72 rnm    I*4            unique star identification number       (20)
52 73-74 zn2    I*2            zone number of UCAC2 (0 = no match)     (21)
53 75-78 rn2    I*4            running record number along UCAC2 zone  (21)
         12 bytes
---------------------------------------------------------------------------
         78 = total number of bytes per star record

Note  (1):  Positions are on the International Celestial Reference
    System (ICRS) as represented by the Hipparcos / Tycho-2  catalogs.
    The epoch for the positions of all stars is J2000.0; the weighted
    mean catalog position was updated using the provided proper
    motions.  The observational UCAC position is but one of several
    going into these values and is not given in the UCAC4; thus the
    original UCAC observation cannot be recovered from these data. 
    The declination is given in south pole distance (spd) and can be
    converted back to a true declination by subtracting 324000000 mas.

Note  (2):  Unknown, or unrealistic photometric results are set to 
    magnitude = 20 (20000 mmag entry in catalog).  Systematic errors
    are expected to be below 0.1 mag for magm,maga photometric results
    obtained from the UCAC CCD pixel data.  The aperture photometry
    is considered more robust, particularly for "odd" cases, while 
    the model fit magnitude is expected to be more accurate for 
    "well behaved" stars.

Note  (3):  A value of 99 for error in magnitude means "no data".
    For many stars a photometric error based on the scatter from 
    individual observations of that star on different CCD frames
    could be obtained.  A model error was also attempted to be
    assigned, based on the S/N ratio.  The error quoted here is
    the larger of the 2.  If that error exceeds 0.9 mag the error
    was set to 0.9 mag (= value 90 in catalog data, unit = 10 mmag).

Note  (4):  The object type flag is used to identify possible problems 
    with a star or the source of data.  Of the individual image flags
    the one with the largest value (worst problem case) is propagated
    into this object type flag, unless it is superseded by an overriding
    flag at the combined image stage.
    The object type flag has the following meaning:

    0 = good, clean star (from MPOS), no known problem
    1 = largest flag of any image = near overexposed star (from MPOS)
    2 = largest flag of any image = possible streak object (from MPOS)
    3 = high proper motion (HPM) star, match with external PM file (MPOS)
    4 = actually use external HPM data instead of UCAC4 observ.data
          (accuracy of positions varies between catalogs)
    5 = poor proper motion solution, report only CCD epoch position
    6 = substitute poor astrometric results by FK6/Hip/Tycho-2 data
    7 = added supplement star (no CCD data) from FK6/Hip/Tycho-2 data,
         and 2 stars added from high proper motion surveys
    8 = high proper motion solution in UCAC4, star not matched with PPMXL
    9 = high proper motion solution in UCAC4, discrepant PM to PPMXL
     (see discussion of flags 8,9 in redcution section 2e above)

    The number of stars with those flags is:
      0 103080317
      1   2785787
      2     15519
      3     97042
      4     35073
      5   5004704
      6     34473
      7      8925
      8   2028600
      9    689653

Note  (5):  The cdf flag is a combined double star flag used to indicate 
    the type/quality of double star fit.  It is a combination of 2 flags,
    cdf = 10 * dsf + dst  with the following meaning:

    dsf = double star flag = overall classification
      0 = single star
      1 = component #1 of "good" double star
      2 = component #2 of "good" double star
      3 = blended image

    dst = double star type, from pixel data image profile fits,
           largest value of all images used for this star
      0 = no double star, not sufficient #pixels or elongation
           to even call double star fit subroutine
      1 = elongated image but no more than 1 peak detected
      2 = 2 separate peaks detected -> try double star fit
      3 = secondary peak found on each side of primary
      4 = case 1 after successful double fit (small separ. blended image)
      5 = case 2 after successful double fit (most likely real double)
      6 = case 3 after successful double fit (brighter secondary picked)

    A word of caution: often a dsf= 1 or 2 image is paired with a dsf= 3.
    If for a star any of the several images reveals a "blended image",
    that higher dsf=3 flag is carried into the output file.  This can
    happen for a regular double star with unique components 1 and 2.
    A flag dsf=3 means this could be component 1 or 2 but at least on
    one CCD frame a blended image was detected.  This blend could be
    with the other component, or a spurious image or artifact.
    The double star flags need to be interpreted with caution; anything
    but a zero means "likely some double star component or blended image".

    The following number of stars have cdf flag values:
     0 110299935
     1    262162   
     2    355769
     3     57103
     4      2408     14   19590     24  23967     34 1053229
     5      7884     15   78344     25 209822     35 1226761
     6     23787     16   13096     26  20945     36  125291

Note  (6):  The range of values here is 1 to 255 which is represented
    as a signed 1-byte integer (range -127 to 127); thus add 128 to the
    integer number found in the data file.  There is no 0 mas value;
    data less than 1 mas have been set to 1 mas.  Original data larger
    than 255 mas have been set to 255.  
    If the astrometric data for a star was substituted from an external
    catalog like Hipparcos, Tycho or high proper motion data, a mean
    error in position and proper motion depending on the catalog and
    magnitude of the star was adopted.

Note  (7):  A zero for the number of used images indicates that all images
    have some "problem" (such as overexposure). In that case an unweighted
    mean over all available images (na) is taken to derive the mean
    position, while normally a weighted mean was calculated based on
    the "good" images, excluding possible problem images (nu <= na).

Note  (8):  A value of 32767 for either proper motion component means
    the real PM of that star is larger and found in the extra table
    file  u4hpm.dat (32 stars, ASCII).  The cross reference is established
    by the unique, modified MPOS number (column 51 of main data file),
    which is also given on the HPM supplement stars file.
    For stars without valid proper motion the proper motion data are set
    to 0.  However, valid proper motions can also be 0.  The "no data"
    case is indicated by the sigma proper motion columns (see below).

Note  (9):  Values in the binary data files are represented as signed,
    1-byte integer (range -128 to 127).  Add 128 to bring those values
    to the range of 0 to 255, which is the error in proper motion in
    unit of 0.1 mas/yr, with the following exception.
    Data entries above 250 indicate larger errors as follows:
    251 --> 275 = 27.5 mas/yr
    252 --> 325 = 32.5 mas/yr
    253 --> 375 = 37.5 mas/yr
    254 --> 450 = 45.0 mas/yr
    255 --> "no data" = set to 500 for output tables

    For astrometric data copied from the FK6, Hipparcos and Tycho-2
    catalogs a mean error in positions was adopted depending on input
    catalog and the brightness of the star rather than giving the 
    individual star's error quoted in those catalogs.

Note (10):  The 2MASS items copied into UCAC4 are described at
    pegasus.astro.umass.edu/ipac_wget/releases/allsky/doc/sec2_2a.html

Note (11):  For each 2MASS bandpass a combined flag was created
    (cc_flg*10 + ph_qual) consisting of the contamination flag (0 to 5) 
    and the photometric quality flag (0 to 8).  

    0 =  cc_flg  2MASS 0, no artifacts or contamination
    1 =  cc_flg  2MASS p, source may be contaminated by a latent image
    2 =  cc_flg  2MASS c, photometric confusion
    3 =  cc_flg  2MASS d, diffraction spike confusion
    4 =  cc_flg  2MASS s, electronic stripe
    5 =  cc_flg  2MASS b, bandmerge confusion

    0 =  no ph_qual flag
    1 =  ph_qual 2MASS X, no valid brightness estimate
    2 =  ph_qual 2MASS U, upper limit on magnitude
    3 =  ph_qual 2MASS F, no reliable estimate of the photometric error
    4 =  ph_qual 2MASS E, goodness-of-fit quality of profile-fit poor
    5 =  ph_qual 2MASS A, valid measurement, [jhk]snr>10 AND [jhk]cmsig<0.10857
    6 =  ph_qual 2MASS B, valid measurement, [jhk]snr> 7 AND [jhk]cmsig<0.15510
    7 =  ph_qual 2MASS C, valid measurement, [jhk]snr> 5 AND [jhk]cmsig<0.21714
    8 =  ph_qual 2MASS D, valid measurement, no [jhk]snr OR [jhk]cmsig req.

    For example icqflg = 05 is decoded to be cc_flg=0, and ph_qual=5, meaning
    no artifacts or contamination from cc_flg and 2MASS qual flag = "A" .

Note (12):  The photometric errors from 2MASS were rounded by 1 digit
    here to fit into fewer bytes (1/100 mag instead of millimag).
    These data were taken from the j_msigcom, h_msigcom, and k_msigcom columns
    of the 2MASS point source catalog.  See note (10).

Note (13):  Data are from the AAVSO Photometric all-sky survey (APASS)
    DR6 plus single observation stars kindly provided by A.Henden.
    A magnitude entry of 20000 indicates "no data".  For bright stars
    the  apasm(1) = B mag and apasm(2) = V mag columns contain the
    Hipparcos/Tycho Bt and Vt mags respectively, whenever there is no
    APASS B or V available and valid Bt or Vt mags were found.
    For the bright supplement stars the same was done.  All thses cases
    are identified by apasm(1) < 20000 and apase(1) = 0  for B mags,
    and similarly for apasm(2) < 20000 and apase(2) = 0  for V mags.
    For over 10,000 stars no Vt mag was available and the V mag from Tycho
    was used instead.  

    The following number of stars have entries in the APASS photometry:
      51862350  some valid data in either of the columns
      51070044  B mag 
      51861015  V mag 
      45799843  g mag
      45615993  r mag
      41491953  i mag

Note (14):  Positive errors are from the official release data error
    estimates (at least 2 observations per star).  Formal, S/N estimated
    errors for single observations are multiplied by -1 for this column.
    The valid range for each APASS magnitude error is +-90 = +-0.90 mag.
    For "no data" (i.e. magnitude = 20000 = 20.0 mag) the error is set to 99.

Note (15):  The g-flag from the Yale San Juan first epoch Southern
    Proper Motion data (YSJ1, SPM) has the following meaning:

     0 = no info
     1 = matched with 2MASS extended source list
     2 = LEDA  galaxy
     3 = known QSO

    The c-flag from the Yale San Juan first epoch Southern
    Proper Motion data (YSJ1, SPM) indicates which input catalog
    has been used to identify stars for pipeline processing:

     1 = Hipparcos
     2 = Tycho2
     3 = UCAC2
     4 = 2MASS psc
     5 = 2MASS xsc (extended sources, largely (but not all!) galaxies)
     6 = LEDA  (confirmed galaxies, Paturel et al. 2005)
     7 = QSO   (Veron-Cetty & Veron 2006)

Note (16, 17) binary data:  a single 4-byte integer is used to store
    the 10 flags of "icf".  That 4-byte integer has the value:
       icf = icf(1)*10^8 + icf(2)*10^7 + ...  + icf(8)*10 + icf(9)

Note (16):  The FK6-Hipparcos-Tycho-source-flag has the following meaning:
            (= icf(1))
     0 = not a Hip. or Tycho star
     1 = Hipparcos 1997 version main catalog (not in UCAC4 data files)
     2 = Hipparcos double star annex
     3 = Tycho-2
     4 = Tycho annex 1
     5 = Tycho annex 2
     6 = FK6 position and proper motion (instead of Hipparcos data)
     7 = Hippparcos 2007 solution position and proper motion
     8 = FK6      only PM substit. (not in UCAC4 data)
     9 = Hipparcos 2007, only proper motion substituted

Note (17):  The catflg match flag is provided for major catalogs used
     in the computation of the proper motions.  Each match is analyzed
     for multiple matches of entries of the 1st catalog to 2nd catalog 
     entries, and the other way around.  Matches are also classified
     by separation and difference in magnitude to arrive at a confidence
     level group.  The flag has the following meaning: 

     0 = star not matched with this catalog
     1 = unique-unique match,  not involving a double star
     2 =  ... same, but involving a flagged double star
     3 = multiple match but unique in high confidence level group, no double
     4 =  ... same, but involving a flagged double star
     5 = closest match, not involving a double, likely o.k. 
     6 =  ... same, but involving a flagged double star
     7 = maybe o.k. smallest sep. match in both directions, no double
     8 =  ... same, but involving a flagged double star

Note (18):  This flag is either 0 (no match) or contains the log10 of
    the apparent total diameter for I-band (object size) information
    (unit = 0.1 arcmin) copied from the LEDA catalog (galaxies).  
    A size value of less than 1 has been rounded up to 1.

Note (19):  This flag is either 0 (no match) or contains the length of
    the semi-major axis of the fiducial ellipse at the K-band 
    (object size) information copied from the 2MASS extended source
    catalog. 

Note (20):  This unique star identification number is between 200001
    and  321640 for Hipparcos stars, and between 1 and 9430 for non-
    Hipparcos stars supplemented to the UCAC4 catalog (no CCD observ.).
    For all other stars this unique star identification number is the
    internal mean-position-file (MPOS) number + 1 million.
    For both the Hipparcos and the supplement stars there is an entry
    on the u4supl.dat file providing more information, including the
    original Hipparcos star number.  Note, there are several thousand
    cases where different UCAC4 stars link to the same Hipparcos star
    number due to resolved binary stars with each component being a 
    separate star entry in UCAC4.

Note (21):  This info provides a cross reference to UCAC2.  Both zn2
    and rn2 are 0 if the UCAC4 star could not be found in UCAC2.


5b) Min/max and check sums
--------------------------
The following table gives the total range of values in the UCAC4 main
data (all zone files from z001 to z900), as well as the value used for 
"no data".  Dashes in the "no data" column indicate that all stars in 
the UCAC4 catalog have valid entries for that column.

col        min        max no_data description
------------------------------------------------------------------
  1         14 1295999997    ---  mean RA  [mas]
  2      57955  647908447    ---  mean SPD [mas]
  3       4012      20000  20000  UCAC4 model    mag [mmag]
  4       4168      20000  20000  UCAC4 aperture mag [mmag]
  5          0         99     99  std.err. UCAC4 mag [1/100 mag]
  6          0          9    ---  object type flag 
  7          0         36    ---  double star flag
  8          1        255    255  sigma RAc at central epoch [mas]
  9          1        255    255  sigma Dec at central epoch [mas]
 10          0         71    ---  total number of CCD images
 11          0         70    ---  n. of CCD images used for mean pos.
 12          1         38    ---  n. of catalogs (epochs) used for PM
 13       4707      10438    ---  c. epoch RA  [1/100 yr] after 1900
 14       4700      10438    ---  c. epoch Dec [1/100 yr] after 1900
 15     -32603      32767      0  PM RA*cosD [0.1 mas/yr]
 16     -31574      32767      0  PM Dec     [0.1 mas/yr]
 17          3        500    500  sigma PM RAc [0.1 mas/yr]
 18          3        500    500  sigma PM Dec [0.1 mas/yr]
 19          0 1340037129      0  2MASS star ID number
 20      -2989      20000  20000  2MASS Jmag [mmag]
 21      -4007      20000  20000  2MASS Hmag [mmag]
 22      -4378      20000  20000  2MASS Kmag [mmag]
 23          0         58      0  2MASS J icqflag
 24          0         58      0  2MASS H icqflag
 25          0         58      0  2MASS K icqflag
 26          0         99     99  2MASS Jmag error [1/100 mag]
 27          0         99     99  2MASS Hmag error [1/100 mag]
 28          0         99     99  2MASS Kmag error [1/100 mag]
 29      -1087      20000  20000  APASS B mag [mmag]
 30      -1440      20000  20000  APASS V mag [mmag]
 31       6787      20000  20000  APASS g mag [mmag]
 32       5943      20000  20000  APASS r mag [mmag]
 33       5030      20000  20000  APASS i mag [mmag]
 34        -90         99     99  APASS B mag err [1/100 mag]
 35        -90         99     99  APASS V mag err [1/100 mag]
 36        -90         99     99  APASS g mag err [1/100 mag]
 37        -90         99     99  APASS r mag err [1/100 mag]
 38        -90         99     99  APASS i mag err [1/100 mag]
 39          0         37      0  gc flag SPM
 40          0          9      0  Hipparcos, Tycho source flag
 41          0          8      0  catflg  AC2000
 42          0          8      0  catflg  AGK2 Bonn
 43          0          8      0  catflg  AGK2 Hamburg
 44          0          8      0  catflg  ZA
 45          0          8      0  catflg  BY
 46          0          8      0  catflg  Lick
 47          0          8      0  catflg  NPM
 48          0          8      0  catflg  SPM
 49          0         99      0  leda galaxy flag
 50          0         82      0  2MASS extend.source cat. flag
 51          1  182895114    ---  unique star ID number, modif. MPOS
 52          0        288      0  UCAC2 zone number (if matched)
 53          0     270171      0  UCAC2 ID number along zone
------------------------------------------------------------------

The following table lists the check sums (values by column added up
over all stars of all zone files):

column               sum
------------------------
   1   85882199747838282
   2   32643520088752491
   3       1734468736192
   4       1740985078524
   5          4308258441
   6            50976753
   7            92617964
   8          7643548652
   9          7536811002
  10           328481210
  11           309920557
  12           259941559
  13       1032282199626
  14       1035570477588
  15         -1844702259
  16         -2768938466
  17          8658847664
  18          8631255911
  19   72112460953700603
  20       1567909305940
  21       1515037852115
  22       1500861673912
  23           758275797
  24           759920399
  25           766630946
  26           482284101
  27           589716513
  28           805257995
  29       2055936882771
  30       2006791306640
  31       2054419812064
  32       2023198315779
  33       2028163598478
  34          6272154501
  35          6276616263
  36          6877032078
  37          6895992388
  38          7370094121
  39           202509190
  40             8065088
  41             6564698
  42              444076
  43             1533211
  44             6258173
  45             4320949
  46             1413660
  47           102261525
  48            93499864
  49              552401
  50              569057
  51    9760101273905336
  52          7112160417
  53       4726488342991
------------------------

 
5c) Other files (in u4i folder)
---------------
zone_stats = summary info for each zone

  This is a text file (ASCII) giving summary information about
  stars in each zone.  There are some header lines describing 
  the contents, followed by a table with 900 lines, 1 each for 
  a 0.2 deg wide zone in declination, beginning at the South 
  Celestial Pole.

  zn     = zone number
  nsz    = number of stars in zone
  nss    = accumulated sum of stars this and prev.zones
  nopmz  = numb. of stars in zone without proper motion
  no2mz  = numb. of stars in zone without 2MASS match
  max_dec= largest declination of zone

   zn       nsz       nss     nopmz     no2mz   max_dec
  -----------------------------------------------------
    1       206       206         5         3    -89.80
    2       660       866         8        17    -89.60
    3      1143      2009        16        42    -89.40
  ...
  898       834 113779410        82       118     89.60
  899       512 113779922        39        67     89.80
  900       171 113780093         9        20     90.00
  -----------------------------------------------------


u4index.asc = index file for each 1 minute in RA

  This is a text file (ASCII).  Each line contains data for an area
  of sky (bin) 0.2 deg wide in declination (indexed by zone number from
  1 to 900), and 1 minute (0.25 deg) wide in RA (indexed from 1 to 1440).
  So there are a total of 900 * 1440 = 1.296 million lines in this file.
  The meaning of the columns are:

  1  n0   = running star number (index along the main data file)
              of the star before the first one in this bin,
              the sequence starts out with 0 at the beginning of
              each new declination zone
  2  nn   = number of stars in this bin (which can be zero)
  3  zn   = zone number (1 to 900)
  4  j    = index for bins along RA (1 to 1440) 
  5  dec  = upper declination of corresponding zone, 
              printed out only at the beginning of a new zone

  sample data:
      n0   nn  zn    j   dec
       0    0   1    1 -89.8
       0    0   1    2
       0    0   1    3
       0    0   1    4
       0    1   1    5
       1    0   1    6
         ...
  138282   28 450 1438
  138310   22 450 1439
  138332   26 450 1440
       0   23 451    1   0.2
      23   31 451    2
      54   14 451    3
         ...
     171    0 900 1438
     171    0 900 1439
     171    0 900 1440


u4index.unf = same as  u4index.asc  but as unformatted, binary file:
  
      INTEGER*4 n0(900,1440), nn(900,1440)  

      OPEN (15,FILE=fnidxu,ACCESS='direct',RECL=5184000)  ! 900 * 1440 * 4
      READ (15,REC=1) n0
      READ (15,REC=2) nn

  Thus each item is a 4-byte integer, the first half of the file contains
  the values for 'n0', the 2nd half contains values for nn.
  In each half, the data is stored as all values for the particular RA index
  value for all zones. That is, there is a series of 1440 blocks, with each 
  block containing the values for 900 declination zones.


u4hpm.dat = data for very high proper motion stars (ASCII)

  This file was needed for all stars with a proper motion component
  outside the +-3276.6 mas/yr range (limit for 2-byte integers).
  The proper motion in the main data is set to 32767 [0.1 mas/yr]
  to identify such a case.  The star then can be identified by rnm
  or zn,rnz in this file to pick up the actual proper motion.
  The file is sorted by running star number.
     
      rnm   = unique star identifier (col. 51 main data)
      zn    = UCAC4 zone number for this star
      rnz   = running record number along that zone for this star
      pmrc  = actual proper motion in RA*cos(dec) [0.1 mas/yr]
      pmd   = actual proper motion in Dec         [0.1 mas/yr]
      RA    = col. 1 of main data file (redundant)
      SPD   = col. 2 of main data file (redundant)
      maga  = col. 4 of main data file (redundant)

        rnm  zn    zrn    pmrc     pmd         RA        SPD  maga
          1 644 101666   41087   31413 1140226325  463469832 20000
          2 644 101659   41558   32586 1140206623  463496356 20000
     200137 137 116044  -37758    7655  782744223   98353832 20000
        ...
  106363470 469  47857  -37060  -11490  734876481  337245686 14457
  110589580 486  52878  -38420  -27250  590832393  349252344 13155
  113038183 494  48937   10990  -51230  442763714  355581607 12513


u4supl.dat = data for supplement stars and cross reference to Hipparcos

  This ASCII file contains all UCAC4 stars which have a match to the
  Hipparcos catalog and all stars which are supplemented, i.e. those
  without UCAC CCD observations (bright stars).  There are 128631 lines 
  (unique stars) in this file.  These are exactly all those stars with 
  unique star number (col.51 of the main data)  rnm < 1,000,000.

         1         0      0  0  7  0
         2         0      0  0  7  0
         3         0      0  3  7  0
         4         0      0  3  7  0
       ...
      7019         0      0  3  7  0
      7020         0      0  3  7  0
      7021         0      0  3  7  0
    200001         0  43636  2  7  0
    200002         0 107949  2  7  0
    200003         0 115928  2  7  0
       ...
    321607 181870335  11767  7  6  3
    321608 181882163 101884  6  3  0
    321609 181883754   3128  7  3  0
    321610 181888489  47953  7  3  0

   col  explanation
     1  rnm  = unique star ID number from UCAC4 (col. 51 main data)
     2  MPOS = mean position file (CCD data) internal record number,
                 or zero if not observed with UCAC astrograph
     3  hipn = Hipparcos Catalogue star number
     4  htsf = FK6-Hipparcos-Tycho source flag (see note 15 main file)
     5  objt = object type flag (see note 4 main file)
     6  stfl = substitute flag, accumulated from following cases:
           0 = no substitute data, use UCAC if available, else external data,
       else replace UCAC position, proper motion by external data because of:
           1 = no "good" image from CCD observation
           2 = star is flagged as blended image
           4 = position difference to external data is too large (> 50 mas)


hipsupl.dat = Hipparcos data, 2nd release (van Leeuwen, 2007), ASCII,
              for all Hipparcos stars identified in UCAC4 data

  This file contains 121610 lines (stars) with Hipparcos data linked
  to the "rnm" unique star ID number of UCAC4 (col. 51 of main data).
  The rnm for those stars are in the range between 200,001 and 321610.
  The file contains the following columns.  For more explanations see
  (van Leeuwen, 2007, or cdsarc.u-strasbg.fr/viz-bin/Cat?I/311).

   col  explanation
     1  rnm = unique star ID number from UCAC4 (col. 51 main data)
     2  MPOS star number (internal running number all mean position stars)
            note: stars supplemented (no CCD observations) have MPOS = 0
     3  original Hipparcos star ID number
     4  solution type flag new reductions
     5  ...                old ...
     6  number of components 
     7  RA [hour] at epoch 1991.25 on ICRS
     8  Dec [deg] ...
     9  parallax [mas]
    10  proper motion RA*cosDec  [mas/yr]
    11  proper motion Dec        [mas/yr]
    12  formal error on RA       [mas] 
    13  ...             Dec      [mas]
    14  ...             parallax [mas]
    15  ...             PM RAc   [mas]
    16  ...             PM Dec   [mas]
    17  number of field transits used
    18  goodness of fit
    19  percentage rejected data
    20  cosmic dispersion added (stochastic solution)
    21  entry in one of the supplement catalogs
    22  Hipparcos magnitude (Hp)
    23  error of Hp 
    24  scatter of Hp
    25  reference to variability annex
    26  (B-V) color index  
    27  formal error on (B-V)
    28  (V-I) color index

  For 109 of these stars there are no 2007 solution Hipparcos data
  and corresponding columns are set to zero.


u4xtycho = cross reference to Tycho-2
   
   This ASCII text file contains 2549788 lines and is sorted by column 2.

   col  explanation
     1  Tycho-2 star number (all 3 parts together in single, long integer)
     2  (UCAC4 zone number) * 1,000,000 + UCAC4 record number along zone
     3  rnm = unique star ID number from UCAC4 (col. 51 main data)
  

6) Description of utility software
----------------------------------
Fortran code and sample output is provided in the folder "access".
There are 2 main programs (u4dump.f and u4access.f) and some additional
files with subroutines.  Compile string examples are given in the 
comment section on top of each main program file.  The programs can
be run from the command line interactively with instructions provided.

u4dump.f  = read binary zone file(s), output all data items to ASCII file
            with optional column separator character

u4access.f= example code to access UCAC4 data as function of RA,Dec,mag
            range and output with various formats; utilizing the index file

u4sub.f   = file with subroutines used in u4dump and u4access

u4.tab    = sample output from a run of u4access

z001.asc  = sample output in ASCII of first zone, comma as column separator
z900.asc  = sample output in ASCII of last  zone, blank as column separator

These algorithms were also translated into C thanks to Bill Gray (Project
Pluto, Guide software, www.projectpluto.com/ucac4.htm), who provided the
following files:

makefile,  u4test.c,  ucac4.c,  ucac4.h

This C code has the same functionality as the Fortran code explained above.


7) Acknowledgments
------------------
In a project such as this that has spanned over more than a decade and 
two continents, many people have been involved.  Here we list people 
and their main contributions to the project.

Norbert Zacharias - Principle Investigator, observer, responsible for
  UCAC position reductions (from pixels to positions on the sky),
  observing schedule, and external position comparisons, re-processing
  of pixel data and development of reduction pipeline, Star-Scan plate
  measure reductions.

Ted Rafferty - former project lead, instrumentation specialist, observer,
  main author of the astrograph history document.

Charlie Finch - re-reduction of CCD x,y to RA,Dec, including systematic
  error control, high proper motion stars, match to 2MASS and
  photometric calibration.

Marion Zacharias - quality control, observer, early position reductions,
   extragalactic link program.

Terry Girard - SPM data reductions, from x,y plate data to RA,Dec,
   incl. systematic error handling (magnitude equations) and all
   related reduction pipeline development.

Bill van Altena - for many years of collaboration in astrometry.

Burton Jones, Bob Hanson, and Arnold Klemola - NPM data.

Arne Henden - for sharing unpublished APASS data and photometric consulting.

Dave Monet - measurements of SPM and NPM plates on PPM at NOFS.

Nigel Hambly - for collaboration on high proper motion stars and big
    contributions to UCAC3 proper motions.

Gary Wycoff - proper motions data preparation, star identifications
   (up to UCAC3).

Sean Urban - proper motions of UCAC2 release, preparation of
  other position catalogs including systematic error removal
  and compiling a merged catalog of Hipparcos and Tycho data.

Valeri Makarov - reduction of AGK2 and other StarScan plate measures
   from global x,y to individual RA,Dec positions.

Jennifer Bartlet - assisting in taking out original PMM x,y measures 
   mapping parameters to arrive at raw x,y data of NPM plates

Brian Mason, Bill Hartkopf - new double star speckle observations with
   USNO 26in and data reductions.

Bill Hartkopf and Marion Zacharias - preparing the history section
   of the UCAC4 DVD release.

David Hall - verification of tape archive, early data handling.

Marvin Germain - telescope control system software development,
  software-to-hardware interface including electronics.

Ellis Holdenried - telescope control system software maintenance
  and extension of user interface for telescope operation.

Lars Winter - basis for astrometric CCD reduction software.

Greg Hennessy - computer administration support (USNO-DC)

Danilo Castillo - observer,  Cerro Tololo, Chile

Mauricio Martinez - observer,  Cerro Tololo, Chile

Sergio Pizarro - observer,  Cerro Tololo, Chile

Oscar Saa - telescope manager, Cerro Tololo, Chile

Trudy Tilleman - observer, U.S. Naval Observatory Flagstaff Station,
     assist with data reductions.

Stephanie Potter - observer, U.S. Naval Observatory Flagstaff Station.

Dominic Marcello - observer, U.S. Naval Observatory Flagstaff Station.

Gary Wieder - telescope upgrades, relocation, maintenance, and
  troubleshooting.

John Pohlman - telescope upgrades, maintenance, and troubleshooting.

John Bowles - telescope upgrades and maintenance (USNO-DC).

Dave Smith - telescope upgrades and maintenance (USNO-DC).

Tie Siemers - telescope upgrades and maintenance (USNO-DC).

Mike Divittorio - telescope maintenance and troubleshooting (NOFS).

Steve Sell - telescope maintenance and troubleshooting (NOFS).

Albert Rhodes - telescope maintenance and troubleshooting (NOFS).

Blaize Canzian - network and computer system support (NOFS).

Sumit Dutta - summer student working on separating out minor planet
      observations and streak artifacts.

Aleida Young - summer student working on identification of high
      proper motion stars.

Danley Hsu - summer student working on UCAC3 release, and providing
      the UCAC3 access code, assist with checking of UCAC4.



Other Acknowledgments
---------------------
This project would not have been possible without support from the USNO
administration.  Special thanks are given to Thomas Corbin for under-
standing the need for such a project, articulating it to those with
resources, and administering the project in its early days.  Thanks
are given to the three USNO Astrometry Department Heads during the
UCAC project.  Without their continued support, it would not have come
to fruition.  They are F. Stephen Gauss, Ted Rafferty, and Ralph Gaume.
Thanks are given to the USNO Scientific Director, Kenneth Johnston, for
his approval and support of the project and for making USNO resources
available.

Our successful stay at the Cerro Tololo Interamerican Observatory would 
not have been possible without a memorandum of understanding, worked out
by P. Kenneth Seidelmann, USNO, and the then director of CTIO, Malcolm 
Smith.  The entire staff of CTIO is thanked for their generous support.
The same holds for all the people at the Naval Observatory Flagstaff
Station (NOFS) during the northern hemisphere observing of UCAC,
in particular we want to thank the then director Jeff Pier.

Also, we wish to thank Gary Sims from Spectral Instruments for building
our 4k camera and providing outstanding support over the years.
Finally, we wish to acknowledge the following for their important
contributions;  Bill Gray for custom updates to his GUIDE software
regarding minor planet ephemerids, and Rae Stiening (Univ.of Mass.) 
for providing preliminary and pre-release 2MASS data to our project.

We are grateful for the many, constructive comments we received from
the following testers of the UCAC4-beta data: 

Rae Stiening (from 2MASS)
Dave Herald, Dave Gault (Australia)
Rama Teixeira (Sao Paulo, Brazil)
Christine Ducourant (Bordeaux, France)
Bill Gray (Project Pluto)
Steve Preston (from occultation community)
Ricky Smart (Torino, Italy)
Sean Urban, Greg Hennessy, Paul Barrett (USNO)
Bob Zavala (NOFS)

Aladin and Vizier were invaluable tools provided through CDS, Strasbourg.


8) References 
-------------
This list includes items cited in the above text and is supplemented by 
listing all publications related to UCAC production and presentations. 
Many of the papers listed here can be found in the "papers" subdirectory
on the distribution DVD with the aim to document the history of the project.

Boyd,M.R., Winters,J.G., Henry,T.J., et al. 2011,
  "The solar neighborhood XXV. Discovery of new proper motion stars
   with 0.40 "/yr > mu > 0.18 "/yr between declinations -47 and 00",
   AJ 142, 10

Deacon,N. R., Hambly,N. C., Cooke,J. A. 2005
  "Southern infrared proper motion survey. I. Discovery of new high
  proper motion stars from first full hemisphere scan"
  A&A, 435, 363

ESA 1997,
   "The Hipparcos and Tycho Catalogues", European Space Agency,
   publication SP-1200

Finch,Charlie T., Henry,Todd J., Subasavage,John P., Jao,Wei-Chun,
Hambly,Nigel C., 2007,
   "The Solar Neighborhood. XVIII. Discovery of New Proper-Motion
    Stars with 0.40" yr-1 > µ >= 0.18" yr-1 between Declinations -90° 
    and -47°", AJ, 133, 2898

Finch,C., Zacharias,N., Girard,T., Wycoff,G.L., Zacharias,M.I. 2009,
  "UCAC3 is coming!"
  AAS

Finch,C., Zacharias,N., Wycoff,G., 2010,
   "UCAC3 atrometric reductions", AJ 139, 2200

Finch,C., Zacharias,N., Henry,T., 2010
   "UCAC3 Proper Motion Survey. I. Discovery of New Proper Motion
    Stars in UCAC3 with 0."40 yr-1 > µ = 0.0018 yr^-1 between Declinations
    -90 degrees and -47 degrees", AJ 140, 844 

Finch, C.T., Zacharias, N., Boyd, M.R., Henry, T.J., Hambly, N.C., 2012
   "UCAC3 Proper Motion Survey. II. Discovery Of New Proper Motion Stars In
    UCAC3 With 0.40" yr^-1 > mu >= 0.18" yr^-1 Between Declinations -47 deg
    and 00 deg", in press ApJ, 745, 118

Gauss,F.S. Zacharias,N. Rafferty,T.J. Germain,M.E. Holdenried,E.R.
Pohlman,J.W. and Zacharias,M.I., 1996,
   "A new astrometric survey of the Southern Hemisphere",
    Bull.AAS 28, No.4, p.1282

Girard,T. M., Platais,I., Kozhurina-Platais,V., van Altena, W. F.,
Lopez, C. E. 1998,
  "The Southern Proper Motion Program. I.  Magnitude-Equation Correction",
   AJ 115, 855.

Girard,T.M., van Altena,W.F., Zacharias,N., Viera,K., Casetti-Dinescu,D.I.,
Castillo,D., Herrera,D., Lee,Y.S., Beers,T.C., Monet,D.G., Lopez,C.E. 2011,
  "The Southern Proper Motion Program IV. The SPM4 Catalog",
   AJ 142, 15

Hartkopf,W.I., Mason,B.D., Wycoff,G.L., Finch,C.T., Zacharias,N., 2010, 
  "Double Stars in the UCAC3 Catalog", BAAS 41,280, AAS Meeting #215,419.24

Hoeg,E., Fabricius,C., Makarov,V.V., Urban,S., Corbin,T., Wycoff,G.,
Bastian,U., 2000,
   "The Tycho-2 Catalogue of the 2.5 million brightest
   stars", A&A 355L, 27 (short paper)

Hoeg,E., Fabricius,C., Makarov,V.V., Bastian,U., Schwekendieck,P.,
Wicenec,A., Urban,S., Corbin,T., Wycoff,G., 2000,
   "Construction and verification of the Tycho-2 Catalogue",
   A&A 357, 367 (long paper)

Hambly,N. C., MacGillivray,H. T., Read,M. A., Tritton,S. B.,
Thomson,E. B., Kelly,B. D., Morgan,D. H., Smith,R. E., Driver,S. P.,
Williamson,J., Parker,Q. A., Hawkins,M. R. S., Williams,P. M.,
Lawrence,A. 2001,
  "The SuperCOSMOS Sky Survey - I. Introduction and description",
  MNRAS, 326, 1279

Hambly,N. C., Irwin,M. J., MacGillivray,H. T. 2001,
  "The SuperCOSMOS Sky Survey - II. Image detection, parametrization,
  classification and photometry", MNRAS, 326, 1295

Hambly,N. C., Davenhall,A. C., Irwin,M. J., MacGillivray, H. T. 2001,
  "The SuperCOSMOS Sky Survey - III. Astrometry",
  MNRAS. 326, 1315

Henry,Todd J., Subasavage,John P., Brown,Misty A., Beaulieu,Thomas D.,
  Jao,Wei-Chun, Hambly,Nigel C. 2004,
  "The Solar Neighborhood. X. New Nearby Stars in the Southern Sky
   and Accurate Photometric Distance Estimates for Red Dwarfs",
  AJ. 128, 2460

Jones, B. F., Hanson, R. B., and Klemola, A. R., 2000,
   "Lick Northern Proper Motion Program: NPM2", AAS meeting 196
   abstract 53.02

Kovalevsky,J., Lindegren,L., Perryman,M.A.C. et al. 1997,
  "The Hipparcos Catalogue as a realisation of the extragalactic
   reference system",   A&A 323, 620

Lepine,S., Shara,M.M. 2005,
  "A Catalog of Northern Stars with Annual Proper Motions Larger than
   0.15" (LSPM-NORTH Catalog)", AJ. 129, 1483

Lepine,S., 2008
  "New High Proper Motion Stars from the Digitized Sky
  Survey. Iv. Completion of the Southern Survey and 170 Additional Stars
  with mu > 0.45'' yr", AJ, 135, 2177

Paturel,G., Vauglin,I., Petit,C., Borsenberger,J., Epchtein,N.,
Fouque,P., Mamon,G. 2005,
  "A catalog of LEDA galaxies with DENIS measurements"
   A&A, 430, 751

Platais,I., Girard,T. M., Kozhurina-Platais,V., van Altena,W. F.,
Lopez,C. E., Mendez,R. A., Wen-Zhang Ma, Ting-Gao Yang,
MacGillivray,H. T., Yentis,D. J. 1998,
  "The Southern Proper Motion Program. II. A Catalog at the South
  Galactic Pole",  AJ, 116, 2556

Rafferty,T., Zacharias,N. 1999,
  "USNO CCD Astrograph Catalog - South",
  Acta Historica Astronomiae, 6, 175-178

Roeser,S., Demleitner,M., Schilbach,E. 2010,
  "The PPMXL catalog of positions and proper motions on the ICRS.
   Combining USNO-B1.0 and the two micron all sky survey(2MASS)",
   AJ 139, 2440

Ruiz,Maria Teresa, Wischnjewsky,Marina, Rojo,Patricio M.,
Gonzalez,Luis E. 2001,
  "Calan-ESO Proper-Motion Catalog", ApJ 133, 199

Salim S., Gould A. 2003
  "Improved astrometry and photometry for the Luyten catalog.
  II. Faint stars and the revised catalog.", AJ, 582, 1011

Scholz,R.-D., Irwin,M., Ibata,R., Jahreiß,H., Malkov,O. Yu. 2000,
  "New high-proper motion survey in the Southern sky",
  A&A 353, 958

Skrutskie,M. F., Cutri,R. M., Stiening,R., Weinberg,M. D., Schneider,S.,
Carpenter,J. M., Beichman,C., Capps,R., Chester,T., Elias,J., Huchra,J.,
Liebert,J., Lonsdale,C., Monet,D. G., Price,S., Seitzer,P., Jarrett,T.,
J.D. Kirkpatrick, J. Gizis, E. Howard, T. Evans, J. Fowler, L. Fullmer,
Hurt,R., Light,R., Kopan,E. L., Marsh,K. A., McCallon,H. L., Tam,R.,
Van Dyk,S., Wheelock,S. 2006,
  "The Two Micron All Sky Survey (2MASS)",  AJ, 131, 1163

Subasavage,John P., Henry,Todd J., Hambly,Nigel C., Brown,Misty A.,
Jao,Wei-Chun 2005,
  "The Solar Neighborhood. XII. Discovery of New High Proper Motion
  Stars with µ >= 0.4" yr-1 between Declinations -90° and -47°",
  AJ, 129, 413

Subasavage,John P., Henry,Todd J., Hambly,Nigel C., Brown,Misty, A.,
Jao,Wei-Chun, Finch,Charlie T. 2005
  "The Solar Neighborhood. XV. Discovery of New High Proper Motion
  Stars with µ >= 0.4" yr-1 between Declinations -47° and 00°",
  AJ, 130, 1658

van Leeuwen, F. 2007,
  "Hipparcos, the new reduction of the raw data",
   Springer Science Library, Vol. 350

Veron-Cetty & Veron 2006, A&A 455, 773

Wroblewski, H. and Torres, C. 1989
  "New proper-motion stars south of declination -40 deg and right
  ascension between 00 H and 04 H 30 M", A&A 78, 231

Wroblewski, H. and Torres, C. 1991
  "New proper-motion stars south of declination -40 deg and right
  ascension between 04h 30m and 16h 00m", A&A 91, 129

Wroblewski, H. and Torres, C. 1994
  "New proper-motion stars south of declination -40deg and right
  ascension between 16h and 24h", A&A 105, 179

Wroblewski, H. and Torres, C. 1996
  "New proper-motion stars with declination between -5deg and -30deg
  and right ascension between 0h and 9h.",
  A&A 115, 481

Wroblewski, H. and Torres, C. 1997
  "New proper-motion stars with declination between -5d and -30d and
  right ascension between 9h and 13h 30m`",
  A&A 122, 447

Wroblewski, H. and Costa, E. 1999
  "New high proper motion stars with declinations between -5(deg) and
  -30(deg) , and right ascensions between 13h 30m and 24h",
  A&A 139, 25

Wroblewski, H. and Costa, E. 2001
  "High proper motion stars with declinations between -30o and -40o,
  and right ascensions between 00 h and 10 h 40 m",
  A&A 367, 725

Urban,S. E., Corbin,T. E., Wycoff,G. L., Martin,J. C., Jackson,E. S.,
Zacharias,M. I., Hall,D. M. 1998,
  "The AC 2000: The Astrographic Catalogue on the System Defined by
  the HIPPARCOS Catalogue"
  AJ, 115, 1212

Urban, S.E., Wycoff, G.L., Makarov, V.V., 2000,
   "Comparisons of Tycho-2 Catalogue Proper Motions with Hipparcos
   and ACT", AJ 120, 501

Urban,S.E., Corbin, T.E., Wycoff, G.L., Makarov, V.V., Hoeg, E.,
Fabricius, C., 2000,
   "The AC 2000.2 Catalogue", AAS meeting 199, abstract #129.04

Urban,S.E., Zacharias,N., Wycoff,G.L, 2004,
  "The UCAC2 Bright Star Supplement",
  Vizier on-line data catalog I/294A, 2004yCat.1294....0U

van Altena, W. F., Girard, T. M., Platais, I., Kozhurina-Platais, V.,
Ostheimer, J., Lopez, C. E., and Mendez, R. A., 1999,
   "The Yale/San Juan Southern Proper Motion Program", 
    AAS DDA meeting #31, #10.04

Wielen,R., Schwan,H., Dettbarn,C., Lenhardt,H. Jahreiss,H., Jahrling,R.,
   1999, "Sixth Catalogue of Fundamental stars (FK6)",
   Veroeff. Astron. Rechen-Inst. Heidelberg 35, 1

Winter, L. 1999
   Ph.D thesis, University of Hamburg (in German), CCD image reductions

Zacharias, N. 1997,
   "Astrometric Quality of the USNO CCD Astrograph (UCA)", AJ 113, 1925

Zacharias, N., Germain, M.E., Rafferty, T.J., 1997,
   "UCAC-S: a New High Precision, High Density Astrometric Catalog in
   the Southern Hemisphere", in: Proceedings "Hipparcos Venice 97",
   ESA publication SP-402, p.177

Zacharias,N.
  "Extension of the optical reference frame: ground based",
   IAU GA 1997, JD7, invited review talk 
   in Highlights of Astronomy, Vol.11A, p.300-303,
   ed. J.Andersen, Kluver Acad.Publ. 1998

Zacharias,N. Corbin,T.E. Zacharias,M.I Rafferty,T.J.
Seidelmann,P.K. Gauss,F.S., 1998 (BAAS 30 No.4, p.1368)
   "High Precision Astrometry for the Hubble Deep Field - South",
   poster preseted at the AAS meeting #193, Austin, TX, Jan. 1999

Zacharias, N. Zacharias, M.I., 1999,
   "Data Structure and Software of the UCAC-S Project", in: Proceedings
   of ADASS VIII, Eds. D.M.Mehringer,  R.L.Plante and D.A.Roberts,
   ASP Conf.series 172, p.345, San Francisco

Zacharias,N. Urban,S.E. 1999,
  "The first year of the UCAC-S project",
  AAS, DDA meeting #31, paper #10.05

Zacharias, N., Urban, S.E., Zacharias, M.I., Hall, D.M., Wycoff, G.L.,
Rafferty, T.J., Germain, M.E., Holdenried, E.R., Pohlman, J.,
Gauss, F.S., Monet, D., Winter, L., 2000,
   "The first US Naval Observatory CCD Astrograph Catalog", AJ 120, 2131

Zacharias, N., Zacharias, M.I., Urban, S.E. and Hoeg, E., 2000,
   "Comparing Tycho-2 astrometry with UCAC1", AJ 120, 1148

Zacharias,N., Rafferty,T.J., Urban,S.E., Zacharias,M.I., Wycoff,G.L. 2000,
  "The UCAC as input catalog for FAME",
  proceed. IAU Coll. 180, p.80, Washington, DC,
  Eds. K.J.Johnston, D.D.McCarthy, B.J.Luzum, G.H.Kaplan

Zacharias,N., Zacharias,M.I., Rafferty,T.J. 2001,
  "Status of the UCAC project",
  abstract, DDA meeting #32, Houston, TX

Zacharias, N., 2002,
   "Astrometric surveys in support of large telescopes", 
   Proceed. SPIE 4836, 279, Eds. T.A. Tyson & S. Wolff

Zacharias,N., Zacharias,M.I., Urban,S.E., Rafferty,T.J. 2002,
  "UCAC2: a new high precision catalog of positions and proper motions",
  abstract, AAS meeting 199, Washingtion DC, Jan. 2002

Zacharias,N., Zacharias,M.I. 2002,
  "Systematic error corrections for UCAC2 positions",
  presentation at the DDA meeting, Mt.Hood

Zacharias, N. 2003,
   "The USNO CCD Astrograph Catalog (UCAC) Project and Beyond", in
   "Small Telescopes in the New Millennium II.  The Telescopes We Use.",
   ed. T Oswald, Kluwer Acad. Publ

Zacharias, N., Urban, S., Rafferty, T., Holdenried, E., and Winter, L.,
   2003, "First Results from AGK2 Plate Remeasurements", 
   BAAS 35 #4, p.1036, abstract #6.01, 34th DDA meeting Ithaca, NY

Zacharias,N. 2004,
  "Astrometric reference stars: from UCAC to URAT",
   proceed. 3rd Potsdam Thinkshop on robotic telescopes,
   AN 325, 631 

Zacharias,N., Urban,S.E., Zacharias,M.I., Wycoff,G.L., Hall,D.M.,
   Monet,D.G., Rafferty,T.J. 2004,
  "The Second US Naval Observatory CCD Astrograph Catalog (UCAC2)"
  AJ 127, 3043 (May)

Zacharias,N., Zacharias,M.I. 2007,
  "The USNO CCD Astrograph Catalog (UCAC)",
  poster for USNO display (unpublished)

Zacharias,N., Finch,C., Wycoff,G., Hartkopf,W.
  "Improving Hipparcos Proper Motions with UCAC",
  DDA meeting 2009, Virginia Beach, 2009,
  BAAS 41 No.2, p. 910, abstract #16.03

Zacharias, M.I., Zacharias, N. 2009,
  "Significant radio-optical reference frame offsets from CTIO data",
  poster paper at the IAU XXVII Gen.Ass., Rio de Janeiro, Brazil

Zacharias,N., Finch,C., Girard,T., Hambly,N., Wycoff,G.
Zacharias,M.I., Castillo,D., Corbin,T.  et al.
  2009yCat.1315...0Z, data release of UCAC3, CDS catalog I/315, Aug 2009

Zacharias,N. 2010,
   "UCAC3 pixel processing", AJ 139, 2208-2217

Zacharias,N., Finch,C., Girard,T., Hambly,N., Wycoff,G., Zacharias,M.I.,
Castillo,D., Corbin,T., DiVittorio,M., Dutta,S., Gaume,R., Gauss,S.,
Germain,M., Hall,D., Hartkopf,W., Hsu,D., Holdenried,E., Makarov,V.,
Martines,M., Mason,B., Monet,D., Rafferty,T., Rhodes,A., Siemers,T.,
Smith,D., Tilleman,T., Urban,S., Wieder,G., Winter,L, Young,A., 2010
  "The Third US Naval Observatory CCD Astrograph Catalog (UCAC3)",
   AJ 139, 2184-2199

Zacharias,N. Gaume,R. 2010,
  "UCAC and URAT: optical astrometric catalog observing programs",
   proceed. Journees meeting Paris 2010 (in press) Ed. N.Capitaine,
   http://syrte.obspm.fr/jsr/journees2010/index.php?page=proceedings

Zacharias,N., Finch,C., Zacharias,M.I., Girard,T. 2011,
  "UCAC4 status", poster paper presented at the AAS 218, Boston,MA

-------------------------------------------------------------------
Enjoy UCAC4 and Live Long And Prosper !
Norbert Zacharias for the UCAC team, USNO, Washington DC, July 2012 
-------------------------------------------------------------------

